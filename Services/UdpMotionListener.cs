using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Vamsync.Models;

namespace Vamsync.Services;

public sealed class UdpMotionListener : IAsyncDisposable
{
    private const int ListenPort = 15601;
    private readonly ILogger<UdpMotionListener> _logger;
    private readonly AppState _appState;
    private readonly IReadOnlyList<IUdpMotionParser> _parsers;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Lock _statsLock = new();
    private readonly Queue<DateTimeOffset> _recentPacketTimes = new();
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Task? _statsTask;
    private UdpClient? _udpClient;
    private long _totalPacketsReceived;

    public UdpMotionListener(
        ILogger<UdpMotionListener> logger,
        AppState appState,
        IEnumerable<IUdpMotionParser> parsers)
    {
        _logger = logger;
        _appState = appState;
        _parsers = parsers.ToArray();
    }

    public event EventHandler<MotionFrame>? MotionReceived;

    public bool IsRunning => _listenTask is not null && !_listenTask.IsCompleted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning)
            {
                return;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, ListenPort));
            _listenTask = ListenLoopAsync(_udpClient, _cts.Token);
            _statsTask = StatsLoopAsync(_cts.Token);
            _appState.SetListening(true, $"Listening on 127.0.0.1:{ListenPort}");
            _appState.ResetUdpPacketStats();
            _appState.AddLog($"UDP listener started on 127.0.0.1:{ListenPort}.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_cts is null)
            {
                return;
            }

            _cts.Cancel();
            _udpClient?.Close();

            if (_listenTask is not null)
            {
                try
                {
                    await _listenTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (_statsTask is not null)
            {
                try
                {
                    await _statsTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            _cts.Dispose();
            _cts = null;
            _udpClient?.Dispose();
            _udpClient = null;
            _listenTask = null;
            _statsTask = null;
            ClearPacketStats();
            _appState.SetListening(false, "Stopped");
            _appState.AddLog("UDP listener stopped.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task ListenLoopAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(cancellationToken);
                var receivedAt = DateTimeOffset.UtcNow;
                if (!TryParseMotionFrame(
                    result.Buffer,
                    receivedAt,
                    out var frame,
                    out var rejectionReason))
                {
                    if (!string.IsNullOrWhiteSpace(rejectionReason))
                    {
                        _appState.AddLog($"Ignored invalid UDP packet: {rejectionReason}");
                    }

                    continue;
                }

                RegisterReceivedPacket(frame.ReceivedAt);
                MotionReceived?.Invoke(this, frame);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UDP listener crashed.");
            _appState.SetError(ex.Message);
            _appState.AddLog($"UDP listener error: {ex.Message}");
            _appState.SetListening(false, "Error");
        }
    }

    private bool TryParseMotionFrame(
        ReadOnlySpan<byte> payload,
        DateTimeOffset receivedAt,
        out MotionFrame frame,
        out string? rejectionReason)
    {
        var rejectionReasons = new List<string>();
        var ignoredByRecognizedParser = false;

        foreach (var parser in _parsers)
        {
            if (parser.TryParse(payload, receivedAt, out var parsedFrame, out var parserRejectionReason)
                && parsedFrame is not null)
            {
                frame = parsedFrame;
                rejectionReason = null;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(parserRejectionReason))
            {
                rejectionReasons.Add(parserRejectionReason);
            }
            else
            {
                ignoredByRecognizedParser = true;
            }
        }

        frame = null!;
        rejectionReason = ignoredByRecognizedParser
            ? null
            : rejectionReasons.Count == 0
                ? "No UDP motion parser accepted the payload."
                : string.Join(" ", rejectionReasons.Distinct());
        return false;
    }

    private async Task StatsLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            PublishPacketStats(DateTimeOffset.UtcNow);
        }
    }

    private void RegisterReceivedPacket(DateTimeOffset receivedAt)
    {
        lock (_statsLock)
        {
            _totalPacketsReceived++;
            _recentPacketTimes.Enqueue(receivedAt);
            TrimRecentPacketTimes(receivedAt);
            _appState.SetUdpPacketStats(_recentPacketTimes.Count, _totalPacketsReceived);
        }
    }

    private void PublishPacketStats(DateTimeOffset now)
    {
        lock (_statsLock)
        {
            TrimRecentPacketTimes(now);
            _appState.SetUdpPacketStats(_recentPacketTimes.Count, _totalPacketsReceived);
        }
    }

    private void TrimRecentPacketTimes(DateTimeOffset now)
    {
        while (_recentPacketTimes.Count > 0 && now - _recentPacketTimes.Peek() > TimeSpan.FromSeconds(1))
        {
            _recentPacketTimes.Dequeue();
        }
    }

    private void ClearPacketStats()
    {
        lock (_statsLock)
        {
            _recentPacketTimes.Clear();
            _totalPacketsReceived = 0;
            _appState.ResetUdpPacketStats();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleLock.Dispose();
    }
}
