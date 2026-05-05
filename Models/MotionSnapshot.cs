namespace Vamsync.Models;

public sealed record MotionSnapshot(
    byte Position,
    byte Speed,
    float DurationSeconds,
    DateTimeOffset ReceivedAt)
{
    public const int PacketLength = 6;
    public const byte MaxPosition = 99;
    public const byte MaxSpeed = 99;
    public const float MaxDurationSeconds = 5f;

    public static bool TryParse(
        ReadOnlySpan<byte> packet,
        DateTimeOffset receivedAt,
        out MotionSnapshot? snapshot)
    {
        snapshot = null;

        if (packet.Length != PacketLength)
            return false;

        var position = packet[0];
        var speed = packet[1];
        var duration = BitConverter.ToSingle(packet.Slice(2, 4));

        if (position > MaxPosition)
            return false;

        if (speed > MaxSpeed)
            return false;

        if (!float.IsFinite(duration))
            return false;

        if (duration < 0f || duration > MaxDurationSeconds)
            return false;

        snapshot = new MotionSnapshot(position, speed, duration, receivedAt);
        return true;
    }

    public double Position01 => Position / (double)MaxPosition;

    public double Speed01 => Speed / (double)MaxSpeed;

    public int DurationMillisecondsClamped(int minMs, int maxMs)
    {
        var ms = (int)Math.Round(DurationSeconds * 1000d);
        return Math.Clamp(ms, minMs, maxMs);
    }
}