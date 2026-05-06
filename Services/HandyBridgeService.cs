using handyapiv3.Abstractions;
using Microsoft.Extensions.Logging;
using Vamsync.Models;

namespace Vamsync.Services;

public sealed class HandyBridgeService : IAsyncDisposable
{
    private static readonly TimeSpan MinimumConnectionStatusCheckInterval = TimeSpan.FromSeconds(2);
    private const double AbsoluteMinimumDurationMilliseconds = 100d;
    private const double MaximumTravelUnitsPerSecond = 3.5d;

    private readonly IHandyService _handyService;
    private readonly UdpMotionListener _udpMotionListener;
    private readonly AppState _appState;
    private readonly MotionCsvLogger _motionCsvLogger;
    private readonly ILogger<HandyBridgeService> _logger;
    private readonly Lock _motionStateLock = new();
    private readonly Lock _connectionStatusLock = new();

    private bool _subscribed;
    private bool _checkingConnectionStatus;
    private DateTimeOffset _lastConnectionStatusCheckAt = DateTimeOffset.MinValue;
    private double? _lastHandyPosition;

    public HandyBridgeService(
        IHandyService handyService,
        UdpMotionListener udpMotionListener,
        AppState appState,
        MotionCsvLogger motionCsvLogger,
        ILogger<HandyBridgeService> logger)
    {
        _handyService = handyService;
        _udpMotionListener = udpMotionListener;
        _appState = appState;
        _motionCsvLogger = motionCsvLogger;
        _logger = logger;
    }

    public async Task<bool> ApplyConnectionKeyAsync(string connectionKey, CancellationToken cancellationToken = default)
    {
        _appState.SetConnectionKey(connectionKey);
        _handyService.SetConnectionKey(connectionKey);

        try
        {
            var info = await GetInfoWithLoggingAsync(
                requestSummary: "connection_key_applied=true",
                cancellationToken);
            _appState.SetHandyStatus(
                connected: true,
                status: "Connected",
                deviceInfo: $"{info.HardwareModelName ?? "Handy"} / FW {info.FirmwareVersion ?? "unknown"}");
            _appState.SetError(null);
            _appState.AddLog("Handy connection verified.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify Handy connection.");
            _appState.SetHandyStatus(false, "Connection failed");
            _appState.SetError(ex.Message);
            _appState.AddLog($"Handy connection failed: {ex.Message}");
            await _motionCsvLogger.LogDeviceEventAsync(
                "connection-verify-failed",
                "Failed to verify Handy connection.",
                ex);
            return false;
        }
    }

    public void ClearConnectionKey()
    {
        _handyService.ClearConnectionKey();
        _appState.SetConnectionKey(string.Empty);
        _appState.SetHandyStatus(false, "Not connected", "Unknown");
        
        _appState.SetMappingStatus("Idle");
        _appState.AddLog("Handy connection key cleared.");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_subscribed)
        {
            _udpMotionListener.MotionReceived += OnMotionReceived;
            _subscribed = true;
        }

        await _udpMotionListener.StartAsync(cancellationToken);
        _appState.SetMappingStatus("Waiting for VaM motion");
        _appState.AddLog($"CSV logs are being written to {_motionCsvLogger.LogDirectory}.");
    }

    public async Task StopAsync()
    {
        await _udpMotionListener.StopAsync();
        _appState.SetMappingStatus("Stopped");
        lock (_motionStateLock)
        {
            _lastHandyPosition = null;
        }
    }

    private void OnMotionReceived(object? sender, MotionSnapshot snapshot)
    {
        _ = DispatchMotionAsync(snapshot);
    }

    private async Task HandleMotionAsync(MotionSnapshot snapshot)
    {
        _appState.SetLatestMotion(snapshot);

        if (string.IsNullOrWhiteSpace(_appState.ConnectionKey))
        {
            _appState.SetMappingStatus("Waiting for Handy connection key");
            return;
        }

        _appState.SetError(null);
        var handyPosition = Math.Clamp(snapshot.Position / 99d, 0d, 1d);
        var stopOnTarget = snapshot.Speed <= 2;

        if (snapshot.DurationSeconds <= 0f)
        {
            await _motionCsvLogger.LogMovementSuppressedAsync(
                snapshot,
                handyPosition,
                0d,
                stopOnTarget,
                "Source duration is zero; packet was not forwarded to Handy.");
            _appState.SetMappingStatus(
                $"Ignoring VaM motion with zero source duration: pos {handyPosition:P1}, speed {snapshot.Speed}");
            return;
        }

        double? lastHandyPosition;
        lock (_motionStateLock)
        {
            lastHandyPosition = _lastHandyPosition;
            _lastHandyPosition = handyPosition;
        }

        var durationMilliseconds = ResolveDurationMilliseconds(snapshot, handyPosition, lastHandyPosition);

        var hdspResult = await SendHdspXptWithLoggingAsync(
            handyPosition: handyPosition,
            duration: durationMilliseconds,
            stopOnTarget: stopOnTarget,
            immediateResponse: true);

        await _motionCsvLogger.LogMovementSentAsync(
            snapshot,
            handyPosition,
            durationMilliseconds,
            stopOnTarget);

        _appState.SetMappingStatus(
            $"Mapped VaM motion to Handy HDSP XPT: pos {handyPosition:P1}, duration {durationMilliseconds:F0}ms, result {hdspResult}");
    }

    private static double ResolveDurationMilliseconds(
        MotionSnapshot snapshot,
        double handyPosition,
        double? lastHandyPosition)
    {
        var requestedDurationMilliseconds =
            !float.IsFinite(snapshot.DurationSeconds) || snapshot.DurationSeconds <= 0f
                ? 0d
                : snapshot.DurationSeconds * 1000d;

        var velocityLimitedMinimumDurationMilliseconds = 0d;
        if (lastHandyPosition is not null)
        {
            var positionDelta = Math.Abs(handyPosition - lastHandyPosition.Value);
            velocityLimitedMinimumDurationMilliseconds =
                positionDelta / MaximumTravelUnitsPerSecond * 1000d;
        }

        return Math.Max(
            AbsoluteMinimumDurationMilliseconds,
            Math.Max(requestedDurationMilliseconds, velocityLimitedMinimumDurationMilliseconds));
    }

    private async Task DispatchMotionAsync(MotionSnapshot snapshot)
    {
        try
        {
            await HandleMotionAsync(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to map motion snapshot to Handy commands.");
            await _motionCsvLogger.LogDeviceEventAsync(
                "send-failed",
                "Failed to send Handy HDSP XPT command.",
                ex);
            await CheckHandyConnectionStatusOnTimeoutAsync(ex);
            _appState.SetError(ex.Message);
            _appState.AddLog($"Motion mapping failed: {ex.Message}");
        }
    }

    private async Task CheckHandyConnectionStatusOnTimeoutAsync(Exception ex)
    {
        if (!IsDeviceTimeout(ex))
        {
            return;
        }

        var shouldCheckStatus = false;
        var now = DateTimeOffset.UtcNow;

        lock (_connectionStatusLock)
        {
            if (!_checkingConnectionStatus && now - _lastConnectionStatusCheckAt >= MinimumConnectionStatusCheckInterval)
            {
                _checkingConnectionStatus = true;
                _lastConnectionStatusCheckAt = now;
                shouldCheckStatus = true;
            }
        }

        if (!shouldCheckStatus)
        {
            return;
        }

        try
        {
            _appState.AddLog("Handy API reported device timeout. Checking connection status...");
            await _motionCsvLogger.LogDeviceEventAsync(
                "device-timeout",
                "Handy API reported device timeout. Checking connection status...",
                ex);

            var connected = await GetConnectedWithLoggingAsync(
                "timeout_followup=true");
            if (!connected)
            {
                _appState.SetHandyStatus(false, "Disconnected");
                _appState.SetMappingStatus("Handy device timeout");
                _appState.AddLog("Handy connection check failed after device timeout.");
                await _motionCsvLogger.LogDeviceEventAsync(
                    "connection-check-disconnected",
                    "Handy connection check reported disconnected after device timeout.");
                return;
            }

            var info = await GetInfoWithLoggingAsync(
                requestSummary: "timeout_followup=true",
                cancellationToken: default);
            _appState.SetHandyStatus(
                connected: true,
                status: "Connected",
                deviceInfo: $"{info.HardwareModelName ?? "Handy"} / FW {info.FirmwareVersion ?? "unknown"}");
            _appState.AddLog("Handy connection check succeeded after device timeout.");
            await _motionCsvLogger.LogDeviceEventAsync(
                "connection-check-succeeded",
                "Handy connection check succeeded after device timeout.");
        }
        catch (Exception statusEx)
        {
            _logger.LogWarning(statusEx, "Failed to refresh Handy connection status after device timeout.");
            _appState.SetHandyStatus(false, "Connection check failed");
            _appState.AddLog($"Handy connection check failed after device timeout: {statusEx.Message}");
            await _motionCsvLogger.LogDeviceEventAsync(
                "connection-check-failed",
                "Handy connection check failed after device timeout.",
                statusEx);
        }
        finally
        {
            lock (_connectionStatusLock)
            {
                _checkingConnectionStatus = false;
            }
        }
    }

    private static bool IsDeviceTimeout(Exception ex) =>
        ex.Message.Contains("Device timeout", StringComparison.OrdinalIgnoreCase);

    private async Task<string> SendHdspXptWithLoggingAsync(
        double handyPosition,
        double duration,
        bool stopOnTarget,
        bool immediateResponse,
        CancellationToken cancellationToken = default)
    {
        var requestSummary =
            $"xp={handyPosition:F5},t={duration:F3},stop_on_target={stopOnTarget.ToString().ToLowerInvariant()},immediate_rsp={immediateResponse.ToString().ToLowerInvariant()}";

        try
        {
            var result = await _handyService.SendHdspXptAsync(
                handyPosition,
                duration,
                stopOnTarget,
                immediateResponse,
                cancellationToken);
            await _motionCsvLogger.LogApiCallAsync(
                "hdsp-xpt",
                requestSummary,
                success: true,
                
                cancellationToken: cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            await _motionCsvLogger.LogApiCallAsync(
                "hdsp-xpt",
                requestSummary,
                success: false,
                exception: ex,
                cancellationToken: cancellationToken);
            throw;
        }
    }

    private async Task<bool> GetConnectedWithLoggingAsync(
        string requestSummary,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _handyService.GetConnectedAsync(cancellationToken);
            await _motionCsvLogger.LogApiCallAsync(
                "get-connected",
                requestSummary,
                success: true,
                
                cancellationToken: cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            await _motionCsvLogger.LogApiCallAsync(
                "get-connected",
                requestSummary,
                success: false,
                exception: ex,
                cancellationToken: cancellationToken);
            throw;
        }
    }

    private async Task<handyapiv3.Models.InfoResponse> GetInfoWithLoggingAsync(
        string requestSummary,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _handyService.GetInfoAsync(cancellationToken);
            var responseSummary =
                $"hardware_model={result.HardwareModelName ?? "unknown"},firmware_version={result.FirmwareVersion ?? "unknown"}";
            await _motionCsvLogger.LogApiCallAsync(
                "get-info",
                requestSummary,
                success: true,
                responseSummary: responseSummary,
                cancellationToken: cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            await _motionCsvLogger.LogApiCallAsync(
                "get-info",
                requestSummary,
                success: false,
                exception: ex,
                cancellationToken: cancellationToken);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscribed)
        {
            _udpMotionListener.MotionReceived -= OnMotionReceived;
            _subscribed = false;
        }

        await StopAsync();
    }
}
