using handyapiv3.Abstractions;
using handyapiv3.Models;
using Microsoft.Extensions.Logging;
using Vamsync.Models;

namespace Vamsync.Services;

public sealed class HandyBridgeService : IAsyncDisposable
{
    private const double AbsoluteMinimumDurationMilliseconds = 100d;
    private const double MaximumTravelUnitsPerSecond = 3.00d;
    private const double TCodeOutputIntervalMilliseconds = 250d;
    private const double TCodeSmoothingTimeConstantMilliseconds = 60d;
    private const double TCodeOutputDeadband = 0.003d;
    private const double TCodeStopOnTargetMinimumDelta = 0.010d;

    private readonly IHandyService _handyService;
    private readonly UdpMotionListener _udpMotionListener;
    private readonly AppState _appState;
    private readonly MotionCsvLogger _motionCsvLogger;
    private readonly ILogger<HandyBridgeService> _logger;
    private readonly Lock _motionStateLock = new();
    private readonly Lock _tcodeStateLock = new();

    private bool _subscribed;
    private CancellationTokenSource? _connectionMonitorCts;
    private Task? _connectionMonitorTask;
    private CancellationTokenSource? _tcodeOutputCts;
    private Task? _tcodeOutputTask;
    private double? _lastHandyPosition;
    private MotionFrame? _latestTCodeFrame;
    private double? _smoothedTCodePosition;
    private double? _lastSentTCodePosition;
    private DateTimeOffset? _lastTCodeSmoothingUpdate;

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

        StartTCodeOutputLoop();
        _appState.SetMappingStatus("Waiting for VaM motion");
        if (_motionCsvLogger.Enabled)
        {
            _appState.AddLog($"CSV logs are being written to {_motionCsvLogger.LogDirectory}.");
        }
    }

    public async Task StopAsync()
    {
        await StopConnectionMonitorAsync();
        await StopTCodeOutputLoopAsync();
        await _udpMotionListener.StopAsync();
        _appState.SetMappingStatus("Stopped");
        lock (_motionStateLock)
        {
            _lastHandyPosition = null;
        }

        ResetTCodeState();
    }

    private void OnMotionReceived(object? sender, MotionFrame frame)
    {
        if (frame.Source == MotionFrameSource.TCodeV03)
        {
            StoreLatestTCodeFrame(frame);
            return;
        }

        _ = DispatchMotionAsync(frame);
    }

    private async Task HandleMotionAsync(
        MotionFrame frame,
        double? overridePosition01 = null,
        double minimumRequestedDurationMilliseconds = 0d)
    {
        _appState.SetLatestMotion(frame);

        if (string.IsNullOrWhiteSpace(_appState.ConnectionKey))
        {
            _appState.SetMappingStatus("Waiting for Handy connection key");
            return;
        }

        _appState.SetError(null);
        var handyPosition = Math.Clamp(overridePosition01 ?? frame.Position01, 0d, 1d);
        var suppressedStopOnTarget = frame.Source != MotionFrameSource.TCodeV03 && frame.Speed <= 2;

        if (frame.DurationSeconds <= 0f && minimumRequestedDurationMilliseconds <= 0d)
        {
            await _motionCsvLogger.LogMovementSuppressedAsync(
                frame,
                handyPosition,
                0d,
                suppressedStopOnTarget,
                "Source duration is zero; packet was not forwarded to Handy.");
            _appState.SetMappingStatus(
                $"Ignoring VaM motion with zero source duration: pos {handyPosition:P1}, speed {frame.Speed}");
            return;
        }

        double? lastHandyPosition;
        lock (_motionStateLock)
        {
            lastHandyPosition = _lastHandyPosition;
            _lastHandyPosition = handyPosition;
        }

        var stopOnTarget = ResolveStopOnTarget(frame, handyPosition, lastHandyPosition);
        var durationMilliseconds = ResolveDurationMilliseconds(
            frame,
            handyPosition,
            lastHandyPosition,
            minimumRequestedDurationMilliseconds);

        await SendHdspXptWithLoggingAsync(
            handyPosition: handyPosition,
            duration: durationMilliseconds,
            stopOnTarget: stopOnTarget,
            immediateResponse: true);

        await _motionCsvLogger.LogMovementSentAsync(
            frame,
            handyPosition,
            durationMilliseconds,
            stopOnTarget);

        _appState.SetMappingStatus(
            $"Mapped {frame.SourceLabel} {frame.Axis} to Handy HDSP XPT: pos {handyPosition:P1}, duration {durationMilliseconds:F0}ms");
    }

    private static double ResolveDurationMilliseconds(
        MotionFrame frame,
        double handyPosition,
        double? lastHandyPosition,
        double minimumRequestedDurationMilliseconds)
    {
        var requestedDurationMilliseconds =
            !float.IsFinite(frame.DurationSeconds) || frame.DurationSeconds <= 0f
                ? 0d
                : frame.DurationSeconds * 1000d;
        requestedDurationMilliseconds = Math.Max(
            requestedDurationMilliseconds,
            minimumRequestedDurationMilliseconds);

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

    private static bool ResolveStopOnTarget(
        MotionFrame frame,
        double handyPosition,
        double? lastHandyPosition)
    {
        if (frame.Source != MotionFrameSource.TCodeV03)
        {
            return frame.Speed <= 2;
        }

        if (lastHandyPosition is null)
        {
            return false;
        }

        var positionDelta = Math.Abs(handyPosition - lastHandyPosition.Value);
        return frame.Speed <= 2 && positionDelta >= TCodeStopOnTargetMinimumDelta;
    }

    private async Task DispatchMotionAsync(
        MotionFrame frame,
        double? overridePosition01 = null,
        double minimumRequestedDurationMilliseconds = 0d)
    {
        try
        {
            await HandleMotionAsync(frame, overridePosition01, minimumRequestedDurationMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to map motion frame to Handy commands.");
            await _motionCsvLogger.LogDeviceEventAsync(
                "send-failed",
                "Failed to send Handy HDSP XPT command.",
                ex);
            UpdateHandyStatusFromSendFailure(ex);
            _appState.SetError(ex.Message);
            _appState.AddLog($"Motion mapping failed: {ex.Message}");
        }
    }

    private void StoreLatestTCodeFrame(MotionFrame frame)
    {
        _appState.SetLatestMotion(frame);
        lock (_tcodeStateLock)
        {
            _latestTCodeFrame = frame;
        }
    }

    private void StartTCodeOutputLoop()
    {
        if (_tcodeOutputTask is not null && !_tcodeOutputTask.IsCompleted)
        {
            return;
        }

        _tcodeOutputCts?.Cancel();
        _tcodeOutputCts?.Dispose();
        _tcodeOutputCts = new CancellationTokenSource();
        _tcodeOutputTask = TCodeOutputLoopAsync(_tcodeOutputCts.Token);
    }

    private async Task StopTCodeOutputLoopAsync()
    {
        var cts = _tcodeOutputCts;
        var task = _tcodeOutputTask;

        _tcodeOutputCts = null;
        _tcodeOutputTask = null;

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

    private async Task TCodeOutputLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TCodeOutputIntervalMilliseconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await DispatchLatestTCodeMotionAsync(DateTimeOffset.UtcNow);
        }
    }

    private async Task DispatchLatestTCodeMotionAsync(DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(_appState.ConnectionKey))
        {
            _appState.SetMappingStatus("Waiting for Handy connection key");
            return;
        }

        MotionFrame? frame;
        double smoothedPosition;
        bool shouldSend;

        lock (_tcodeStateLock)
        {
            frame = _latestTCodeFrame;
            if (frame is null)
            {
                return;
            }

            smoothedPosition = SmoothTCodePosition(frame.Position01, now);
            shouldSend = _lastSentTCodePosition is null
                || Math.Abs(smoothedPosition - _lastSentTCodePosition.Value) >= TCodeOutputDeadband;

            if (shouldSend)
            {
                _lastSentTCodePosition = smoothedPosition;
            }
        }

        if (!shouldSend)
        {
            return;
        }

        await DispatchMotionAsync(
            frame,
            overridePosition01: smoothedPosition,
            minimumRequestedDurationMilliseconds: TCodeOutputIntervalMilliseconds);
    }

    private double SmoothTCodePosition(double latestPosition01, DateTimeOffset now)
    {
        if (_smoothedTCodePosition is null || _lastTCodeSmoothingUpdate is null)
        {
            _smoothedTCodePosition = latestPosition01;
            _lastTCodeSmoothingUpdate = now;
            return latestPosition01;
        }

        var elapsedMilliseconds = Math.Max(0d, (now - _lastTCodeSmoothingUpdate.Value).TotalMilliseconds);
        var alpha = 1d - Math.Exp(-elapsedMilliseconds / TCodeSmoothingTimeConstantMilliseconds);
        _smoothedTCodePosition += alpha * (latestPosition01 - _smoothedTCodePosition.Value);
        _lastTCodeSmoothingUpdate = now;
        return _smoothedTCodePosition.Value;
    }

    private void ResetTCodeState()
    {
        lock (_tcodeStateLock)
        {
            _latestTCodeFrame = null;
            _smoothedTCodePosition = null;
            _lastSentTCodePosition = null;
            _lastTCodeSmoothingUpdate = null;
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
