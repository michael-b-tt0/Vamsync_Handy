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
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private UdpClient? _udpClient;

    public UdpMotionListener(ILogger<UdpMotionListener> logger, AppState appState)
    {
        _logger = logger;
        _appState = appState;
    }

    public event EventHandler<MotionSnapshot>? MotionReceived;

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
            _appState.SetListening(true, $"Listening on 127.0.0.1:{ListenPort}");
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

            _cts.Dispose();
            _cts = null;
            _udpClient?.Dispose();
            _udpClient = null;
            _listenTask = null;
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
                if (result.Buffer.Length != 6)
                {
                    _appState.AddLog($"Ignored UDP packet with unexpected length {result.Buffer.Length}.");
                    continue;
                }

                var snapshot = new MotionSnapshot
                {
                    Position = result.Buffer[0],
                    Speed = result.Buffer[1],
                    DurationSeconds = BitConverter.ToSingle(result.Buffer, 2),
                    ReceivedAt = DateTimeOffset.Now,
                };

                MotionReceived?.Invoke(this, snapshot);
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleLock.Dispose();
    }
}
