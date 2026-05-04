using handyapiv3.Abstractions;
using Microsoft.Extensions.Logging;
using Vamsync.Models;

namespace Vamsync.Services;

public sealed class HandyBridgeService : IAsyncDisposable
{
    private static readonly TimeSpan MinimumCommandInterval = TimeSpan.FromMilliseconds(167);
    private readonly IHandyService _handyService;
    private readonly UdpMotionListener _udpMotionListener;
    private readonly AppState _appState;
    private readonly ILogger<HandyBridgeService> _logger;
    private readonly SemaphoreSlim _motionLock = new(1, 1);

    private bool _subscribed;
    private DateTimeOffset _lastHdspCommandAt = DateTimeOffset.MinValue;
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

    private async void OnMotionReceived(object? sender, MotionSnapshot snapshot)
    {
        await _motionLock.WaitAsync();
        try
        {
            await HandleMotionAsync(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to map motion snapshot to Handy commands.");
            _appState.SetError(ex.Message);
            _appState.AddLog($"Motion mapping failed: {ex.Message}");
        }
        finally
        {
            _motionLock.Release();
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
            return 1d;
        }

        return Math.Max(1d, snapshot.DurationSeconds * 1000d);
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscribed)
        {
            _udpMotionListener.MotionReceived -= OnMotionReceived;
            _subscribed = false;
        }

        await StopAsync();
        _motionLock.Dispose();
    }
}
