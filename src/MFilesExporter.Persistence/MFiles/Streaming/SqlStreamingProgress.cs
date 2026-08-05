using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Persistence.MFiles.Streaming;

/// <summary>
/// Progress tick emitted by <see cref="ISqlStreamingEngine"/> to any caller
/// that provides an <see cref="IProgress{T}"/>. Reports low-level engine
/// throughput — separate from the run-wide progress model in the domain
/// layer.
/// </summary>
public sealed record SqlStreamingProgress
{
    /// <summary>Total metadata rows yielded since the run started.</summary>
    public required long RowsYielded { get; init; }

    /// <summary>Number of paginated round-trips executed so far.</summary>
    public required long PagesFetched { get; init; }

    /// <summary>Retry attempts observed since the run started.</summary>
    public required long RetryAttempts { get; init; }

    /// <summary>Most recent cursor observed on the enumeration.</summary>
    public required DocumentFileVersionKey LastCursor { get; init; }

    /// <summary>UTC observation timestamp.</summary>
    public required DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>Time elapsed since the engine started.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>Rows-per-second averaged over the run.</summary>
    public double RowsPerSecond =>
        Elapsed.TotalSeconds > 0 ? RowsYielded / Elapsed.TotalSeconds : 0;
}
