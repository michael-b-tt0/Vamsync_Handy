using System.Globalization;
using System.Text;
using Vamsync.Models;

namespace Vamsync.Services;

public sealed class TCodeMotionParser : IUdpMotionParser
{
    private const int MaxMagnitude = 9999;
    private const double MaxExpectedVelocity01PerSecond = 3.10d;
    private readonly Lock _stateLock = new();
    private double? _lastPosition01;
    private DateTimeOffset? _lastReceivedAt;

    public bool TryParse(
        ReadOnlySpan<byte> payload,
        DateTimeOffset receivedAt,
        out MotionFrame? frame,
        out string? rejectionReason)
    {
        frame = null;
        rejectionReason = null;

        if (!LooksLikeText(payload))
        {
            rejectionReason = "Payload is not TCode text.";
            return false;
        }

        var text = Encoding.ASCII.GetString(payload);
        var commands = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var foundValidTCodeCommand = false;

        foreach (var command in commands)
        {
            if (!TryParseCommand(command, out var axisName, out var magnitude, out var intervalMilliseconds))
            {
                continue;
            }

            foundValidTCodeCommand = true;
            if (!string.Equals(axisName, "L0", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var position01 = magnitude / (double)MaxMagnitude;
            double? velocity01PerSecond;
            lock (_stateLock)
            {
                velocity01PerSecond = CalculateVelocity(position01, receivedAt, intervalMilliseconds);
                _lastPosition01 = position01;
                _lastReceivedAt = receivedAt;
            }

            var speed01 = velocity01PerSecond is null
                ? 0d
                : Math.Clamp(velocity01PerSecond.Value / MaxExpectedVelocity01PerSecond, 0d, 1d);

            frame = new MotionFrame(
                Position01: position01,
                Speed01: speed01,
                DurationSeconds: intervalMilliseconds.GetValueOrDefault() / 1000f,
                ReceivedAt: receivedAt,
                Source: MotionFrameSource.TCodeV03,
                Axis: "L0",
                SourceVelocity01PerSecond: velocity01PerSecond,
                RawPosition: magnitude,
                RawIntervalMilliseconds: intervalMilliseconds);
            return true;
        }

        if (foundValidTCodeCommand)
        {
            return false;
        }

        rejectionReason = "TCode payload did not contain a valid command.";
        return false;
    }

    private double? CalculateVelocity(double position01, DateTimeOffset receivedAt, int? intervalMilliseconds)
    {
        if (_lastPosition01 is null)
        {
            return null;
        }

        if (_lastReceivedAt is not null && receivedAt - _lastReceivedAt.Value > TimeSpan.FromSeconds(1))
        {
            return null;
        }

        var elapsedSeconds = intervalMilliseconds is > 0
            ? intervalMilliseconds.Value / 1000d
            : _lastReceivedAt is null
                ? 0d
                : (receivedAt - _lastReceivedAt.Value).TotalSeconds;
        if (elapsedSeconds <= 0d)
        {
            return null;
        }

        return Math.Abs(position01 - _lastPosition01.Value) / elapsedSeconds;
    }

    private static bool TryParseCommand(
        string command,
        out string axisName,
        out int magnitude,
        out int? intervalMilliseconds)
    {
        axisName = string.Empty;
        magnitude = 0;
        intervalMilliseconds = null;

        if (command.Length < 6)
        {
            return false;
        }

        var axisType = command[0];
        var channel = command[1];
        if (!char.IsAsciiLetter(axisType) || !char.IsAsciiDigit(channel))
        {
            return false;
        }

        axisName = string.Concat(char.ToUpperInvariant(axisType), channel);

        var magnitudeSpan = command.AsSpan(2, 4);
        if (!int.TryParse(magnitudeSpan, NumberStyles.None, CultureInfo.InvariantCulture, out magnitude)
            || magnitude < 0
            || magnitude > MaxMagnitude)
        {
            return false;
        }

        if (command.Length == 6)
        {
            return true;
        }

        if (command[6] != 'I' && command[6] != 'i')
        {
            return false;
        }

        if (command.Length == 7)
        {
            return false;
        }

        if (!int.TryParse(command.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedInterval)
            || parsedInterval < 0)
        {
            return false;
        }

        intervalMilliseconds = parsedInterval;
        return true;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> payload)
    {
        foreach (var value in payload)
        {
            if (value is 9 or 10 or 13 or 32)
            {
                continue;
            }

            if (value is < 48 or > 122)
            {
                return false;
            }
        }

        return payload.Length > 0;
    }
}
