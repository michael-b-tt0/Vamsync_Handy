using Vamsync.Models;

namespace Vamsync.Services;

public sealed class AppState
{
    private const int MaxLogEntries = 80;
    private readonly object _sync = new();
    private readonly List<AppLogEntry> _logs = [];

    public event EventHandler? Changed;

    public string ConnectionKey { get; private set; } = string.Empty;

    public bool IsListening { get; private set; }

    public bool HandyConnected { get; private set; }

    public string HandyStatus { get; private set; } = "Not connected";

    public string ListenerStatus { get; private set; } = "Stopped";

    public string MappingStatus { get; private set; } = "Idle";

    public string LastError { get; private set; } = string.Empty;

    public string DeviceInfo { get; private set; } = "Unknown";

    public MotionSnapshot? LatestMotion { get; private set; }

    public IReadOnlyList<AppLogEntry> Logs
    {
        get
        {
            lock (_sync)
            {
                return _logs.ToArray();
            }
        }
    }

    public void SetConnectionKey(string? connectionKey)
    {
        ConnectionKey = connectionKey?.Trim() ?? string.Empty;
        NotifyChanged();
    }

    public void SetListening(bool isListening, string status)
    {
        IsListening = isListening;
        ListenerStatus = status;
        NotifyChanged();
    }

    public void SetHandyStatus(bool connected, string status, string? deviceInfo = null)
    {
        HandyConnected = connected;
        HandyStatus = status;
        if (!string.IsNullOrWhiteSpace(deviceInfo))
        {
            DeviceInfo = deviceInfo;
        }

        NotifyChanged();
    }

    public void SetMappingStatus(string status)
    {
        MappingStatus = status;
        NotifyChanged();
    }

    public void SetLatestMotion(MotionSnapshot snapshot)
    {
        LatestMotion = snapshot;
        NotifyChanged();
    }

    public void SetError(string? error)
    {
        LastError = error?.Trim() ?? string.Empty;
        NotifyChanged();
    }

    public void AddLog(string message)
    {
        lock (_sync)
        {
            _logs.Insert(0, new AppLogEntry
            {
                Timestamp = DateTimeOffset.Now,
                Message = message,
            });

            if (_logs.Count > MaxLogEntries)
            {
                _logs.RemoveRange(MaxLogEntries, _logs.Count - MaxLogEntries);
            }
        }

        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
