using Vamsync.Models;

namespace Vamsync.Services;

public interface IUdpMotionParser
{
    bool TryParse(
        ReadOnlySpan<byte> payload,
        DateTimeOffset receivedAt,
        out MotionFrame? frame,
        out string? rejectionReason);
}
