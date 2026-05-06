using handyapiv3.Abstractions;
using Microsoft.Extensions.Logging;
using Vamsync.Models;

namespace Vamsync.Services;

public sealed class HandyBridgeService : IAsyncDisposable
{
    private static readonly TimeSpan MinimumCommandInterval = TimeSpan.FromMilliseconds(167);
    private static readonly TimeSpan MinimumConnectionStatusCheckInterval = TimeSpan.FromSeconds(2);
    private const double AbsoluteMinimumDurationMilliseconds = 50d;
    private const double MaximumTravelUnitsPerSecond = 3d;
    
    private const double StopOnTargetPositionDeltaThreshold = 0.005d;
    private const double PositionDeltaSendThreshold = 0.01d;
    private readonly IHandyService _handyService;
    private readonly UdpMotionListener _udpMotionListener;
    private readonly AppState _appState;
    private readonly MotionCsvLogger _motionCsvLogger;
    private readonly ILogger<HandyBridgeService> _logger;
    private readonly Lock _pendingMotionLock = new();
    private readonly Lock _connectionStatusLock = new();

    private bool _subscribed;
    private bool _processingMotion;
    private bool _checkingConnectionStatus;
    private MotionSnapshot? _pendingMotion;
    private DateTimeOffset _lastHdspCommandAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastConnectionStatusCheckAt = DateTimeOffset.MinValue;
    private double? _lastHandyPosition;
    private double? _lastDurationMilliseconds;

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
        _lastHdspCommandAt = DateTimeOffset.MinValue;
        _lastHandyPosition = null;
        _lastDurationMilliseconds = null;
    }

    private void OnMotionReceived(object? sender, MotionSnapshot snapshot)
    {
        var shouldStartProcessor = false;

        lock (_pendingMotionLock)
        {
            // Keep only the most recent motion packet so bursts do not build a stale queue.
            _pendingMotion = snapshot;
            if (!_processingMotion)
            {
                _processingMotion = true;
                shouldStartProcessor = true;
            }
        }

        if (shouldStartProcessor)
        {
            _ = ProcessPendingMotionAsync();
        }
    }

    private async Task ProcessPendingMotionAsync()
    {
        while (true)
        {
            MotionSnapshot? snapshot;

            lock (_pendingMotionLock)
            {
                snapshot = _pendingMotion;
                _pendingMotion = null;

                if (snapshot is null)
                {
                    _processingMotion = false;
                    return;
                }
            }

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
        var durationMilliseconds = ResolveDurationMilliseconds(snapshot, handyPosition, _lastHandyPosition);
        var stopOnTarget = snapshot.Speed <= 2; 
        var now = DateTimeOffset.UtcNow;

        if (!ShouldSendCommand(now, handyPosition, durationMilliseconds, stopOnTarget, out var suppressionReason))
        {
            await _motionCsvLogger.LogMovementSuppressedAsync(
                snapshot,
                handyPosition,
                durationMilliseconds,
                stopOnTarget,
                suppressionReason);
            _appState.SetMappingStatus(
                $"Holding latest VaM motion: pos {handyPosition:P1}, duration {durationMilliseconds:F0}ms");
            return;
        }

        var hdspResult = await SendHdspXptWithLoggingAsync(
            handyPosition: handyPosition,
            duration: durationMilliseconds,
            stopOnTarget: stopOnTarget,
            immediateResponse: true);

        _lastHdspCommandAt = now;
        _lastHandyPosition = handyPosition;
        _lastDurationMilliseconds = durationMilliseconds;
        await _motionCsvLogger.LogMovementSentAsync(
            snapshot,
            handyPosition,
            durationMilliseconds,
            stopOnTarget);

        _appState.SetMappingStatus(
            $"Mapped VaM motion to Handy HDSP XPT: pos {handyPosition:P1}, duration {durationMilliseconds:F0}ms, result {hdspResult}");
    }

    private bool ShouldSendCommand(
        DateTimeOffset now,
        double handyPosition,
        double durationMilliseconds,
        bool stopOnTarget,
        out string suppressionReason)
    {
        suppressionReason = string.Empty;

        if (_lastHandyPosition is null || _lastDurationMilliseconds is null)
        {
            return true;
        }

        var enoughTimeElapsed = now - _lastHdspCommandAt >= MinimumCommandInterval;
        var positionDelta = Math.Abs(handyPosition - _lastHandyPosition.Value);
        var durationDelta = Math.Abs(durationMilliseconds - _lastDurationMilliseconds.Value);

        if (stopOnTarget && positionDelta >= StopOnTargetPositionDeltaThreshold)
        {
            return true;
        }

        if (!enoughTimeElapsed)
        {
            suppressionReason =
                $"Minimum interval not reached; elapsed_ms={(now - _lastHdspCommandAt).TotalMilliseconds:F3}, required_ms={MinimumCommandInterval.TotalMilliseconds:F3}, xp_delta={positionDelta:F5}, duration_delta={durationDelta:F3}, stop_on_target={stopOnTarget.ToString().ToLowerInvariant()}";
            return false;
        }

        if (positionDelta >= PositionDeltaSendThreshold || durationDelta >= 15d)
        {
            return true;
        }

        suppressionReason =
            $"Movement delta below threshold; xp_delta={positionDelta:F5}, duration_delta={durationDelta:F3}, min_xp_delta={PositionDeltaSendThreshold:F5}, min_duration_delta=15.000, stop_on_target={stopOnTarget.ToString().ToLowerInvariant()}";
        return false;
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
                responseSummary: result,
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
                responseSummary: $"connected={result.ToString().ToLowerInvariant()}",
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
