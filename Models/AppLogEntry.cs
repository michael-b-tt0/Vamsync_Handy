namespace Vamsync.Models;

public sealed class AppLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }

    public required string Message { get; init; }
}
