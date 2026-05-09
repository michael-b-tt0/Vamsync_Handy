using Vamsync.Models;

namespace Vamsync.Services;

public sealed class BinaryMotionPacketParser : IUdpMotionParser
{
    public const int PacketLength = 6;
    public const byte MaxPosition = 99;
    public const byte MaxSpeed = 99;
    public const float MaxDurationSeconds = 5f;

    public bool TryParse(
        ReadOnlySpan<byte> payload,
        DateTimeOffset receivedAt,
        out MotionFrame? frame,
        out string? rejectionReason)
    {
        frame = null;
        rejectionReason = null;

        if (payload.Length != PacketLength)
        {
            rejectionReason = $"Binary packet length {payload.Length}; expected {PacketLength}.";
            return false;
        }

        var position = payload[0];
        var speed = payload[1];
        var duration = BitConverter.ToSingle(payload.Slice(2, 4));

        if (position > MaxPosition)
        {
            rejectionReason = $"Binary position {position} exceeds max {MaxPosition}.";
            return false;
        }

        if (speed > MaxSpeed)
        {
            rejectionReason = $"Binary speed {speed} exceeds max {MaxSpeed}.";
            return false;
        }

        if (!float.IsFinite(duration))
        {
            rejectionReason = "Binary duration is not finite.";
            return false;
        }

        if (duration < 0f || duration > MaxDurationSeconds)
        {
            rejectionReason = $"Binary duration {duration:F6}s is outside 0-{MaxDurationSeconds:F1}s.";
            return false;
        }

        frame = new MotionFrame(
            Position01: position / (double)MaxPosition,
            Speed01: speed / (double)MaxSpeed,
            DurationSeconds: duration,
            ReceivedAt: receivedAt,
            Source: MotionFrameSource.BinaryPacket,
            Axis: "binary",
            RawPosition: position);
        return true;
    }
}
