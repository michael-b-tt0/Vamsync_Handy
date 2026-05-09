namespace Vamsync.Models;

public sealed record MotionFrame(
    double Position01,
    double Speed01,
    float DurationSeconds,
    DateTimeOffset ReceivedAt,
    MotionFrameSource Source,
    string Axis,
    double? SourceVelocity01PerSecond = null,
    int? RawPosition = null,
    int? RawIntervalMilliseconds = null)
{
    public const byte MaxDisplayPosition = 99;
    public const byte MaxDisplaySpeed = 99;

    public byte Position => ToDisplayByte(Position01, MaxDisplayPosition);

    public byte Speed => ToDisplayByte(Speed01, MaxDisplaySpeed);

    public string SourceLabel => Source switch
    {
        MotionFrameSource.BinaryPacket => "Binary",
        MotionFrameSource.TCodeV03 => "TCode",
        _ => Source.ToString(),
    };

    public int DurationMillisecondsClamped(int minMs, int maxMs)
    {
        var ms = (int)Math.Round(DurationSeconds * 1000d);
        return Math.Clamp(ms, minMs, maxMs);
    }

    private static byte ToDisplayByte(double value, byte max)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }

        return (byte)Math.Clamp((int)Math.Round(value * max), 0, max);
    }
}

public enum MotionFrameSource
{
    BinaryPacket,
    TCodeV03,
}
