namespace MFilesExporter.Configuration.Options;

/// <summary>
/// Configuration for the File Export Engine — the component that writes a
/// document's binary payload to a filesystem path derived from a
/// configurable <see cref="FolderStrategy"/> and the source's original
/// <c>TITLE + "." + EXTENSION</c>.
/// </summary>
public sealed class FileExportOptions
{
    public const string SectionName = "Exporter:FileExport";

    /// <summary>Root directory. Every exported artifact lives under this path.</summary>
    public string RootPath { get; set; } = "./export-output/documents";

    /// <summary>Folder-layout strategy — see <see cref="FolderStrategyKind"/>.</summary>
    public FolderStrategyKind FolderStrategy { get; set; } = FolderStrategyKind.HashSharded;

    /// <summary>Shard depth for <see cref="FolderStrategyKind.HashSharded"/> (1–4).</summary>
    public int ShardDepth { get; set; } = 2;

    /// <summary>Bucket count for <see cref="FolderStrategyKind.NumericShard"/>.</summary>
    public int NumericBucketCount { get; set; } = 512;

    /// <summary>Date format for <see cref="FolderStrategyKind.Date"/>. Supports Y, M, D placeholders.</summary>
    public string DateFolderPattern { get; set; } = "yyyy/MM";

    /// <summary>Collision-resolution strategy when the sanitized name already exists.</summary>
    public DuplicateResolutionKind DuplicateResolution { get; set; } = DuplicateResolutionKind.IdempotencyKeySuffix;

    /// <summary>Maximum filename length before truncation (bytes / chars).</summary>
    public int MaxFilenameLength { get; set; } = 200;

    /// <summary>Maximum full path length before falling back to shortened hash-based name.</summary>
    public int MaxFullPathLength { get; set; } = 240;

    /// <summary>Fallback string when TITLE is empty.</summary>
    public string DefaultTitle { get; set; } = "untitled";

    /// <summary>Fallback string when EXTENSION is empty. Empty string keeps the file extensionless.</summary>
    public string DefaultExtension { get; set; } = "bin";

    /// <summary>Write buffer size, in bytes.</summary>
    public int WriteBufferSize { get; set; } = 81_920;

    /// <summary>fsync each file after writing. Adds latency; use for durability-critical exports.</summary>
    public bool FsyncOnWrite { get; set; } = true;

    /// <summary>Overwrite an existing file at the final path. Off means the duplicate resolver picks a new name.</summary>
    public bool OverwriteOnCollision { get; set; }
}

/// <summary>Enumerated folder strategies.</summary>
public enum FolderStrategyKind
{
    /// <summary>Everything in the root — one directory.</summary>
    Flat,

    /// <summary>Sharded by SHA-256 hex prefix of the idempotency key.</summary>
    HashSharded,

    /// <summary>Sharded by <c>ID_DOCUMENTFILEPART % NumericBucketCount</c>.</summary>
    NumericShard,

    /// <summary>Grouped by last-write year/month (configurable via <see cref="FileExportOptions.DateFolderPattern"/>).</summary>
    Date,

    /// <summary>Grouped by extension (e.g. <c>pdfs/</c>, <c>docxs/</c>).</summary>
    Category,

    /// <summary>Sharded + date — the recommended shape for &gt; 5 M documents.</summary>
    ShardedByDate,
}

/// <summary>Enumerated duplicate-resolution behaviors.</summary>
public enum DuplicateResolutionKind
{
    /// <summary>Append a suffix derived from the idempotency key (deterministic, race-free).</summary>
    IdempotencyKeySuffix,

    /// <summary>Append <c>_N</c> counter (requires probing — not recommended above ~100 k docs).</summary>
    CounterSuffix,

    /// <summary>Throw on collision (strict mode).</summary>
    Fail,

    /// <summary>Overwrite the existing file.</summary>
    Overwrite,
}
