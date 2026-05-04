using handyapiv3.Abstractions;
using Microsoft.Extensions.Logging;
using Vamsync.Models;

namespace Vamsync.Services;

public sealed class HandyBridgeService : IAsyncDisposable
{
    private static readonly TimeSpan MinimumCommandInterval = TimeSpan.FromMilliseconds(167);
    private static readonly TimeSpan MinimumConnectionStatusCheckInterval = TimeSpan.FromSeconds(2);
    private const double MinimumDurationMilliseconds = 167d;
    private readonly IHandyService _handyService;
    private readonly UdpMotionListener _udpMotionListener;
    private readonly AppState _appState;
    private readonly ILogger<HandyBridgeService> _logger;
    private readonly Lock _pendingMotionLock = new();
    private readonly Lock _connectionStatusLock = new();

    private bool _subscribed;
    private bool _processingMotion;
    private bool _checkingConnectionStatus;
    private MotionSnapshot? _pendingMotion;
    private DateTimeOffset _lastHdspCommandAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastConnectionStatusCheckAt = DateTimeOffset.MinValue;
    private double? _lastPercentPosition;
    private double? _lastDurationMilliseconds;

    public HandyBridgeService(
        IHandyService handyService,
        UdpMotionListener udpMotionListener,
        AppState appState,
        ILogger<HandyBridgeService> logger)
    {
        _handyService = handyService;
        _udpMotionListener = udpMotionListener;
        _appState = appState;
        _logger = logger;
    }

    public async Task<bool> ApplyConnectionKeyAsync(string connectionKey, CancellationToken cancellationToken = default)
    {
        _appState.SetConnectionKey(connectionKey);
        _handyService.SetConnectionKey(connectionKey);

        try
        {
            var info = await _handyService.GetInfoAsync(cancellationToken);
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
    }

    public async Task StopAsync()
    {
        await _udpMotionListener.StopAsync();
        _appState.SetMappingStatus("Stopped");
        _lastHdspCommandAt = DateTimeOffset.MinValue;
        _lastPercentPosition = null;
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

        var percentPosition = Math.Clamp(snapshot.Position / 99d * 100d, 0d, 100d);
        var durationMilliseconds = ResolveDurationMilliseconds(snapshot);
        var stopOnTarget = snapshot.Speed <= 2;
        var now = DateTimeOffset.UtcNow;

        if (!ShouldSendCommand(now, percentPosition, durationMilliseconds, stopOnTarget))
        {
            _appState.SetMappingStatus(
                $"Holding latest VaM motion: pos {percentPosition:F1}%, duration {durationMilliseconds:F0}ms");
            return;
        }

        await _handyService.SendHdspXptAsync(
            percentPosition: percentPosition,
            duration: durationMilliseconds,
            stopOnTarget: stopOnTarget,
            immediateResponse: true);

        _lastHdspCommandAt = now;
        _lastPercentPosition = percentPosition;
        _lastDurationMilliseconds = durationMilliseconds;

        _appState.SetMappingStatus(
            $"Mapped VaM motion to Handy HDSP XPT: pos {percentPosition:F1}%, duration {durationMilliseconds:F0}ms");
    }

    private bool ShouldSendCommand(
        DateTimeOffset now,
        double percentPosition,
        double durationMilliseconds,
        bool stopOnTarget)
    {
        if (_lastPercentPosition is null || _lastDurationMilliseconds is null)
        {
            return true;
        }

        var enoughTimeElapsed = now - _lastHdspCommandAt >= MinimumCommandInterval;
        var positionDelta = Math.Abs(percentPosition - _lastPercentPosition.Value);
        var durationDelta = Math.Abs(durationMilliseconds - _lastDurationMilliseconds.Value);

        if (stopOnTarget && positionDelta >= 0.5d)
        {
            return true;
        }

        if (!enoughTimeElapsed)
        {
            return false;
        }

        return positionDelta >= 1d || durationDelta >= 15d;
    }

    private static double ResolveDurationMilliseconds(MotionSnapshot snapshot)
    {
        if (!float.IsFinite(snapshot.DurationSeconds) || snapshot.DurationSeconds <= 0f)
        {
            return MinimumDurationMilliseconds;
        }

        return Math.Max(MinimumDurationMilliseconds, snapshot.DurationSeconds * 1000d);
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

            var connected = await _handyService.GetConnectedAsync();
            if (!connected)
            {
                _appState.SetHandyStatus(false, "Disconnected");
                _appState.SetMappingStatus("Handy device timeout");
                _appState.AddLog("Handy connection check failed after device timeout.");
                return;
            }

            var info = await _handyService.GetInfoAsync();
            _appState.SetHandyStatus(
                connected: true,
                status: "Connected",
                deviceInfo: $"{info.HardwareModelName ?? "Handy"} / FW {info.FirmwareVersion ?? "unknown"}");
            _appState.AddLog("Handy connection check succeeded after device timeout.");
        }
        catch (Exception statusEx)
        {
            _logger.LogWarning(statusEx, "Failed to refresh Handy connection status after device timeout.");
            _appState.SetHandyStatus(false, "Connection check failed");
            _appState.AddLog($"Handy connection check failed after device timeout: {statusEx.Message}");
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
