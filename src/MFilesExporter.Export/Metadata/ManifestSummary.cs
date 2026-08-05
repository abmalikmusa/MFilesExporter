namespace MFilesExporter.Export.Metadata;

/// <summary>
/// Run-level manifest written once at the end of an export. Downstream
/// consumers should read <c>manifest.json</c> first — it announces the
/// schema version, counts, and paths of every other artifact.
/// </summary>
public sealed record ManifestSummary
{
    /// <summary>Job surrogate identifier from the tracking DB.</summary>
    public required long JobId { get; init; }

    /// <summary>Operator-supplied job label.</summary>
    public required string JobName { get; init; }

    /// <summary>Partition scope for the run.</summary>
    public required string PartitionKey { get; init; }

    /// <summary>Source SQL Server host — captured for audit.</summary>
    public required string SourceServer { get; init; }

    /// <summary>Source database.</summary>
    public required string SourceDatabase { get; init; }

    /// <summary>UTC start time.</summary>
    public required DateTime StartedAtUtc { get; init; }

    /// <summary>UTC completion time (or <c>null</c> if the manifest is written mid-run).</summary>
    public DateTime? CompletedAtUtc { get; init; }

    /// <summary>Aggregate outcome counters.</summary>
    public required ManifestTotals Totals { get; init; }

    /// <summary>References to every metadata artifact produced by the run.</summary>
    public required IReadOnlyList<ManifestArtifactReference> Artifacts { get; init; }
}

/// <summary>Terminal counters, one line each.</summary>
public sealed record ManifestTotals(
    long DocumentsExpected,
    long DocumentsRecorded,
    long Succeeded,
    long Failed,
    long Skipped,
    long TotalBytesWritten);

/// <summary>
/// Pointer from the manifest to an artifact file the exporter produced.
/// Consumers use this to locate <c>metadata.csv</c>, <c>metadata.json</c>,
/// or any other emitted output.
/// </summary>
public sealed record ManifestArtifactReference(
    string RelativePath,
    string Format,
    long RecordCount);
