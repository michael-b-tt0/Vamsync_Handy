namespace Vamsync.Models;

public sealed class MotionSnapshot
{
    public required byte Position { get; init; }

    public required byte Speed { get; init; }

    public required float DurationSeconds { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public double NormalizedPosition => Position / 99d;

    public double NormalizedSpeed => Speed / 99d;
}
