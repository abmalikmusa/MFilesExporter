using MFilesExporter.Domain.Jobs;

namespace MFilesExporter.Domain.Manifest;

/// <summary>
/// The full audit log for one export run — a collection of
/// <see cref="ExportManifestEntry"/> rows plus a small header identifying
/// the job. Manifests are append-only in practice; this domain type carries
/// a materialized snapshot for consumers that need the whole thing.
/// </summary>
/// <remarks>
/// The exporter writes manifests as JSON-lines files on the sink volume so
/// they survive independently of the tracking DB. This domain type is what
/// a reader (verification job, restore tool) reconstructs from those files.
/// </remarks>
public sealed record ExportManifest
{
    /// <summary>Owning job.</summary>
    public required ExportJobId JobId { get; init; }

    /// <summary>Human-readable job label.</summary>
    public required string JobName { get; init; }

    /// <summary>UTC start of the run.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>UTC completion time (or <c>null</c> for open manifests).</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>Immutable, ordered set of entries (append order).</summary>
    public required IReadOnlyList<ExportManifestEntry> Entries { get; init; }

    /// <summary>Convenience: total number of entries.</summary>
    public int Count => Entries.Count;

    /// <summary>Convenience: number of Succeeded entries.</summary>
    public int SucceededCount =>
        Entries.Count(e => e.Status == Documents.ExportStatus.Succeeded);

    /// <summary>Convenience: number of Failed entries.</summary>
    public int FailedCount =>
        Entries.Count(e => e.Status == Documents.ExportStatus.Failed);

    /// <summary>Convenience: number of Skipped entries.</summary>
    public int SkippedCount =>
        Entries.Count(e => e.Status == Documents.ExportStatus.Skipped);

    /// <summary>Convenience: total bytes written across all entries.</summary>
    public long TotalBytesWritten => Entries.Sum(e => e.BytesWritten);
}
