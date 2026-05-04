using handyapiv3.Abstractions;
using Microsoft.Extensions.Logging;
using Vamsync.Models;

namespace Vamsync.Services;

public sealed class HandyBridgeService : IAsyncDisposable
{
    private readonly IHandyService _handyService;
    private readonly UdpMotionListener _udpMotionListener;
    private readonly AppState _appState;
    private readonly ILogger<HandyBridgeService> _logger;
    private readonly SemaphoreSlim _motionLock = new(1, 1);

    private bool _subscribed;
    private bool _hampStarted;
    private double? _lastVelocity;
    private double? _lastStrokeCenter;

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

    public async Task ApplyConnectionKeyAsync(string connectionKey, CancellationToken cancellationToken = default)
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify Handy connection.");
            _appState.SetHandyStatus(false, "Connection failed");
            _appState.SetError(ex.Message);
            _appState.AddLog($"Handy connection failed: {ex.Message}");
            throw;
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

        if (_hampStarted)
        {
            try
            {
                await _handyService.StopHampAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop HAMP while shutting down.");
            }
        }

        _hampStarted = false;
        _lastVelocity = null;
        _lastStrokeCenter = null;
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

        var velocity = Math.Clamp(snapshot.NormalizedSpeed, 0d, 1d);
        var center = Math.Clamp(snapshot.NormalizedPosition, 0d, 1d);

        if (velocity <= 0.02d)
        {
            if (_hampStarted)
            {
                await _handyService.StopHampAsync();
                _hampStarted = false;
                _appState.AddLog("Stopped HAMP due to near-zero incoming speed.");
            }

            _appState.SetMappingStatus($"Paused at {center:P0}");
            return;
        }

        if (!_hampStarted)
        {
            await _handyService.StartHampAsync();
            _hampStarted = true;
            _appState.AddLog("Started HAMP mode.");
        }

        if (_lastStrokeCenter is null || Math.Abs(center - _lastStrokeCenter.Value) >= 0.015d)
        {
            const double halfWindow = 0.01d;
            var min = Math.Clamp(center - halfWindow, 0d, 1d);
            var max = Math.Clamp(center + halfWindow, 0d, 1d);
            await _handyService.SetSliderStrokeAsync(min, max);
            _lastStrokeCenter = center;
        }

        if (_lastVelocity is null || Math.Abs(velocity - _lastVelocity.Value) >= 0.02d)
        {
            await _handyService.SetHampVelocityAsync(velocity);
            _lastVelocity = velocity;
        }

        _appState.SetMappingStatus(
            $"Mapped VaM motion to Handy HAMP: pos {center:P0}, speed {velocity:P0}, duration {snapshot.DurationSeconds:F3}s");
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
