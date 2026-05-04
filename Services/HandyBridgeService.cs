using handyapiv3.Abstractions;
using handyapiv3.Models;
using handyapiv3;
using Microsoft.Extensions.Logging;
using Vamsync.Models;

namespace Vamsync.Services;

public sealed class HandyBridgeService : IAsyncDisposable
{
    private static readonly TimeSpan MinimumCommandInterval = TimeSpan.FromMilliseconds(167);
    private readonly IHandyService _handyService;
    private readonly HandyApiV3Client _handyClient;
    private readonly UdpMotionListener _udpMotionListener;
    private readonly AppState _appState;
    private readonly ILogger<HandyBridgeService> _logger;
    private readonly SemaphoreSlim _motionLock = new(1, 1);

    private bool _subscribed;
    private DateTimeOffset _lastHdspCommandAt = DateTimeOffset.MinValue;
    private double? _lastPercentPosition;
    private double? _lastDurationMilliseconds;
    private DateTimeOffset _lastSliderStateReadAt = DateTimeOffset.MinValue;
    private int _sliderStateReadInFlight;

    public HandyBridgeService(
        IHandyService handyService,
        HandyApiV3Client handyClient,
        UdpMotionListener udpMotionListener,
        AppState appState,
        ILogger<HandyBridgeService> logger)
    {
        _handyService = handyService;
        _handyClient = handyClient;
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

    public async Task<SliderStrokeResponse?> GetSliderStrokeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stroke = await _handyService.GetSliderStrokeAsync(cancellationToken);
            _appState.SetError(null);
            _appState.AddLog(
                $"Loaded Handy stroke limits: {stroke.Min:P0} to {stroke.Max:P0}.");
            return stroke;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Handy stroke settings.");
            _appState.SetError(ex.Message);
            _appState.AddLog($"Loading Handy stroke settings failed: {ex.Message}");
            return null;
        }
    }

    public async Task<SliderStrokeResponse?> SetSliderStrokeAsync(double min, double max, CancellationToken cancellationToken = default)
    {
        try
        {
            var stroke = await _handyService.SetSliderStrokeAsync(min, max, cancellationToken);
            _appState.SetError(null);
            _appState.AddLog(
                $"Applied Handy stroke limits: {stroke.Min:P0} to {stroke.Max:P0}.");
            return stroke;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Handy stroke settings.");
            _appState.SetError(ex.Message);
            _appState.AddLog($"Updating Handy stroke settings failed: {ex.Message}");
            return null;
        }
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
        _lastSliderStateReadAt = DateTimeOffset.MinValue;
        _lastPercentPosition = null;
        _lastDurationMilliseconds = null;
    }

    public async Task ReadSliderStateAsync(CancellationToken cancellationToken = default)
    {
        await ReadSliderStateCoreAsync(null, force: true, cancellationToken);
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
        _appState.SetHdspDiagnostics(
            requestedPercent: percentPosition,
            actualSliderPercent: _appState.ActualSliderPercent,
            status: "Waiting for slider readback");

        _appState.SetMappingStatus(
            $"Mapped VaM motion to Handy HDSP XPT: pos {percentPosition:F1}%, duration {durationMilliseconds:F0}ms");

        TryQueueSliderStateRead(percentPosition);
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

    private void TryQueueSliderStateRead(double requestedPercent)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSliderStateReadAt < TimeSpan.FromMilliseconds(500))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _sliderStateReadInFlight, 1, 0) != 0)
        {
            return;
        }

        _lastSliderStateReadAt = now;
        _ = Task.Run(async () =>
        {
            try
            {
                await ReadSliderStateCoreAsync(requestedPercent, force: false, CancellationToken.None);
            }
            finally
            {
                Interlocked.Exchange(ref _sliderStateReadInFlight, 0);
            }
        });
    }

    private async Task ReadSliderStateCoreAsync(double? requestedPercent, bool force, CancellationToken cancellationToken)
    {
        if (!force && string.IsNullOrWhiteSpace(_appState.ConnectionKey))
        {
            return;
        }

        try
        {
            var response = await _handyClient.GetSliderStateAsync(cancellationToken);
            if (response.Error is not null)
            {
                _appState.SetHdspDiagnostics(
                    requestedPercent: requestedPercent ?? _appState.LastRequestedHdspPercent,
                    actualSliderPercent: _appState.ActualSliderPercent,
                    status: response.Error.Message ?? response.Error.Name ?? "Slider readback failed");
                return;
            }

            var actualPercent = (response.Result?.Value ?? 0d) * 100d;
            var requested = requestedPercent ?? _appState.LastRequestedHdspPercent;
            var status = requested.HasValue
                ? $"Requested {requested.Value:F1}% / actual {actualPercent:F1}%"
                : $"Actual {actualPercent:F1}%";

            _appState.SetHdspDiagnostics(requested, actualPercent, status);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read slider state for diagnostics.");
            _appState.SetHdspDiagnostics(
                requestedPercent: requestedPercent ?? _appState.LastRequestedHdspPercent,
                actualSliderPercent: _appState.ActualSliderPercent,
                status: $"Readback failed: {ex.Message}");
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
        _motionLock.Dispose();
    }
}
