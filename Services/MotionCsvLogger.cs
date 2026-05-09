using System.Globalization;
using System.Text;
using Microsoft.Maui.Storage;
using Vamsync.Models;

namespace Vamsync.Services;

public sealed class MotionCsvLogger
{
    private const string MovementFileName = "handy_movements.csv";
    private const string SuppressedFileName = "suppressed_movements.csv";
    private const string DeviceEventsFileName = "device_events.csv";
    private const string ApiCallsFileName = "handy_api_calls.csv";
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _logDirectory;
    private readonly string _movementFilePath;
    private readonly string _suppressedFilePath;
    private readonly string _deviceEventsFilePath;
    private readonly string _apiCallsFilePath;

    public MotionCsvLogger()
    {
        _logDirectory = Path.Combine(FileSystem.Current.AppDataDirectory, "logs");
        _movementFilePath = Path.Combine(_logDirectory, MovementFileName);
        _suppressedFilePath = Path.Combine(_logDirectory, SuppressedFileName);
        _deviceEventsFilePath = Path.Combine(_logDirectory, DeviceEventsFileName);
        _apiCallsFilePath = Path.Combine(_logDirectory, ApiCallsFileName);
    }

    public bool Enabled
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    public string LogDirectory => _logDirectory;

    public async Task LogMovementSentAsync(
        MotionFrame frame,
        double handyXp,
        double handyDurationMilliseconds,
        bool stopOnTarget,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return;
        }

        var row = string.Join(",",
            Escape(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            Escape(frame.ReceivedAt.ToString("O", CultureInfo.InvariantCulture)),
            Escape(frame.SourceLabel),
            Escape(frame.Axis),
            frame.Position01.ToString("F6", CultureInfo.InvariantCulture),
            frame.Speed01.ToString("F6", CultureInfo.InvariantCulture),
            frame.SourceVelocity01PerSecond?.ToString("F6", CultureInfo.InvariantCulture) ?? string.Empty,
            frame.DurationSeconds.ToString("F6", CultureInfo.InvariantCulture),
            handyXp.ToString("F5", CultureInfo.InvariantCulture),
            handyDurationMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
            stopOnTarget ? "true" : "false");

        await AppendRowAsync(
            _movementFilePath,
            "logged_at_utc,source_received_at_utc,source,axis,source_position_01,source_speed_01,source_velocity_01_per_second,source_duration_seconds,handy_xp,handy_duration_ms,stop_on_target",
            row,
            cancellationToken);
    }

    public async Task LogMovementSuppressedAsync(
        MotionFrame frame,
        double handyXp,
        double handyDurationMilliseconds,
        bool stopOnTarget,
        string suppressionReason,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return;
        }

        var row = string.Join(",",
            Escape(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            Escape(frame.ReceivedAt.ToString("O", CultureInfo.InvariantCulture)),
            Escape(frame.SourceLabel),
            Escape(frame.Axis),
            frame.Position01.ToString("F6", CultureInfo.InvariantCulture),
            frame.Speed01.ToString("F6", CultureInfo.InvariantCulture),
            frame.SourceVelocity01PerSecond?.ToString("F6", CultureInfo.InvariantCulture) ?? string.Empty,
            frame.DurationSeconds.ToString("F6", CultureInfo.InvariantCulture),
            handyXp.ToString("F5", CultureInfo.InvariantCulture),
            handyDurationMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
            stopOnTarget ? "true" : "false",
            Escape(suppressionReason));

        await AppendRowAsync(
            _suppressedFilePath,
            "logged_at_utc,source_received_at_utc,source,axis,source_position_01,source_speed_01,source_velocity_01_per_second,source_duration_seconds,handy_xp,handy_duration_ms,stop_on_target,suppression_reason",
            row,
            cancellationToken);
    }

    public async Task LogDeviceEventAsync(
        string eventType,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return;
        }

        var row = string.Join(",",
            Escape(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            Escape(eventType),
            Escape(message),
            Escape(exception?.GetType().FullName ?? string.Empty),
            Escape(exception?.Message ?? string.Empty));

        await AppendRowAsync(
            _deviceEventsFilePath,
            "logged_at_utc,event_type,message,exception_type,exception_message",
            row,
            cancellationToken);
    }

    public async Task LogApiCallAsync(
        string operation,
        string requestSummary,
        bool success,
        string? responseSummary = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return;
        }

        var row = string.Join(",",
            Escape(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            Escape(operation),
            Escape(requestSummary),
            success ? "true" : "false",
            Escape(responseSummary ?? string.Empty),
            Escape(exception?.GetType().FullName ?? string.Empty),
            Escape(exception?.Message ?? string.Empty));

        await AppendRowAsync(
            _apiCallsFilePath,
            "logged_at_utc,operation,request_summary,success,response_summary,exception_type,exception_message",
            row,
            cancellationToken);
    }

    private async Task AppendRowAsync(
        string filePath,
        string header,
        string row,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_logDirectory);

            if (File.Exists(filePath) && File.ReadLines(filePath).FirstOrDefault() != header)
            {
                File.Move(filePath, ResolveLegacyFilePath(filePath));
            }

            if (!File.Exists(filePath))
            {
                await File.WriteAllTextAsync(
                    filePath,
                    $"{header}{Environment.NewLine}",
                    Encoding.UTF8,
                    cancellationToken);
            }

            await File.AppendAllTextAsync(
                filePath,
                $"{row}{Environment.NewLine}",
                Encoding.UTF8,
                cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string ResolveLegacyFilePath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        return Path.Combine(directory, $"{fileName}.{timestamp}.legacy{extension}");
    }

    private static string Escape(string value)
    {
        if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
