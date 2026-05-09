namespace Vamsync.Models;

public sealed record MotionPacketDecode(
    int PacketLength,
    byte? Position,
    byte? Speed,
    float? DurationSeconds)
{
    public static MotionPacketDecode FromPacket(ReadOnlySpan<byte> packet)
    {
        byte? position = packet.Length >= 1 ? packet[0] : null;
        byte? speed = packet.Length >= 2 ? packet[1] : null;
        float? duration = packet.Length >= 6
            ? BitConverter.ToSingle(packet.Slice(2, 4))
            : null;

        return new MotionPacketDecode(packet.Length, position, speed, duration);
    }
}
