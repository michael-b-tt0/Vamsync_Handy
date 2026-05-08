using handyapiv3.Abstractions;
using handyapiv3.Models;
using Microsoft.Extensions.Logging;
using Vamsync.Models;

namespace Vamsync.Services;

public sealed class HandyBridgeService : IAsyncDisposable
{
    private const double AbsoluteMinimumDurationMilliseconds = 100d;
    private const double MaximumTravelUnitsPerSecond = 3.25d;

    private readonly IHandyService _handyService;
    private readonly UdpMotionListener _udpMotionListener;
    private readonly AppState _appState;
    private readonly MotionCsvLogger _motionCsvLogger;
    private readonly ILogger<HandyBridgeService> _logger;
    private readonly Lock _motionStateLock = new();

    private bool _subscribed;
    private CancellationTokenSource? _connectionMonitorCts;
    private Task? _connectionMonitorTask;
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
            StartConnectionMonitor();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify Handy connection.");
            await StopConnectionMonitorAsync();
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
        _ = StopConnectionMonitorAsync();
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
        if (!string.IsNullOrWhiteSpace(_appState.ConnectionKey))
        {
            StartConnectionMonitor();
        }
        _appState.SetMappingStatus("Waiting for VaM motion");
        _appState.AddLog($"CSV logs are being written to {_motionCsvLogger.LogDirectory}.");
    }

    public async Task StopAsync()
    {
        await StopConnectionMonitorAsync();
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

        await SendHdspXptWithLoggingAsync(
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
            $"Mapped VaM motion to Handy HDSP XPT: pos {handyPosition:P1}, duration {durationMilliseconds:F0}ms");
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
            UpdateHandyStatusFromSendFailure(ex);
            _appState.SetError(ex.Message);
            _appState.AddLog($"Motion mapping failed: {ex.Message}");
        }
    }

    private void StartConnectionMonitor()
    {
        if (_connectionMonitorTask is not null && !_connectionMonitorTask.IsCompleted)
        {
            return;
        }

        _connectionMonitorCts?.Cancel();
        _connectionMonitorCts?.Dispose();
        _connectionMonitorCts = new CancellationTokenSource();
        _connectionMonitorTask = MonitorConnectionStatusAsync(_connectionMonitorCts.Token);
    }

    private async Task StopConnectionMonitorAsync()
    {
        var cts = _connectionMonitorCts;
        var task = _connectionMonitorTask;

        _connectionMonitorCts = null;
        _connectionMonitorTask = null;

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
    }

    private async Task MonitorConnectionStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var eventTypes = new[]
            {
                HandySseEventTypes.DeviceStatus,
                HandySseEventTypes.DeviceConnected,
                HandySseEventTypes.DeviceDisconnected,
                HandySseEventTypes.DeviceError,
            };

            await foreach (var deviceEvent in _handyService.SubscribeToDeviceEventsAsync(
                eventTypes,
                cancellationToken: cancellationToken))
            {
                ApplyHandyDeviceEvent(deviceEvent);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Handy SSE connection monitor failed.");
            _appState.SetHandyStatus(false, "Device event monitor failed");
            _appState.SetMappingStatus("Stopped after Handy SSE monitor error");
            _appState.SetError(ex.Message);
            _appState.AddLog($"Handy SSE monitor failed: {ex.Message}");
            await _motionCsvLogger.LogDeviceEventAsync(
                "sse-monitor-failed",
                "Handy SSE monitor failed.",
                ex);
            await _udpMotionListener.StopAsync();
        }
    }

    private void ApplyHandyDeviceEvent(HandySseEvent deviceEvent)
    {
        switch (deviceEvent.Type)
        {
            case HandySseEventTypes.DeviceStatus:
            case HandySseEventTypes.DeviceConnected:
                SyncHandyStatusFromService("Connected");
                _appState.SetError(null);
                break;

            case HandySseEventTypes.DeviceDisconnected:
                var disconnect = deviceEvent.DeserializeDeviceData<DeviceDisconnectedEventData>();
                _appState.SetHandyStatus(false, "Disconnected");
                if (!string.IsNullOrWhiteSpace(disconnect?.Reason))
                {
                    var message = $"Handy disconnected: {disconnect.Reason}";
                    _appState.SetError(message);
                    _appState.AddLog(message);
                }
                else
                {
                    _appState.AddLog("Handy SSE reported device disconnected.");
                }
                break;

            case HandySseEventTypes.DeviceError:
                var error = deviceEvent.DeserializeDeviceData<ErrorEventData>();
                _appState.SetHandyStatus(_handyService.Connected, _handyService.Connected ? "Device error" : "Disconnected");
                if (!string.IsNullOrWhiteSpace(error?.Message))
                {
                    var message = $"Handy device error: {error.Message}";
                    _appState.SetError(message);
                    _appState.AddLog(message);
                }
                break;
        }
    }

    private void SyncHandyStatusFromService(string connectedStatus)
    {
        var info = _handyService.Info;
        var deviceInfo = info is null
            ? null
            : $"{info.HardwareModelName ?? "Handy"} / FW {info.FirmwareVersion ?? "unknown"}";

        _appState.SetHandyStatus(
            _handyService.Connected,
            _handyService.Connected ? connectedStatus : "Disconnected",
            deviceInfo);
    }

    private void UpdateHandyStatusFromSendFailure(Exception ex)
    {
        if (ex.Message.Contains("Device not connected", StringComparison.OrdinalIgnoreCase))
        {
            _appState.SetHandyStatus(false, "Disconnected");
            _appState.SetMappingStatus("Handy device disconnected");
            return;
        }

        if (ex.Message.Contains("Device timeout", StringComparison.OrdinalIgnoreCase))
        {
            _appState.SetHandyStatus(false, "Device timeout");
            _appState.SetMappingStatus("Handy device timeout");
            return;
        }

        _appState.SetHandyStatus(false, "API error");
        _appState.SetMappingStatus("Handy API error");
    }

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
