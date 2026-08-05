namespace MFilesExporter.Export.Metadata;

/// <summary>
/// One row of the exported metadata catalog. Contains the 13 required fields
/// plus two EDMS-migration extensions (idempotency key + data-file-version).
/// Field names are deliberately source-neutral so migration to a different
/// EDMS is a straightforward mapping exercise — see the migration guide in
/// <c>docs/metadata-generation-framework.md</c>.
/// </summary>
public sealed record MetadataRecord
{
    /* ---- 13 required fields ---- */

    /// <summary>
    /// Source-schema identifier: <c>ID_DOCUMENTFILEPART</c> from the M-Files
    /// vault. Stable across versions of the same document.
    /// </summary>
    public required long DocumentPartId { get; init; }

    /// <summary>
    /// Source-schema identifier: <c>ID_VERSIONPART</c> from the M-Files vault.
    /// Combined with <see cref="DocumentPartId"/> uniquely identifies a version.
    /// </summary>
    public required long VersionPart { get; init; }

    /// <summary>Original title as recorded by the source (verbatim, pre-sanitization).</summary>
    public required string Title { get; init; }

    /// <summary>File extension without leading dot; empty when absent.</summary>
    public required string Extension { get; init; }

    /// <summary>Uncompressed size in bytes.</summary>
    public required long LogicalFileSize { get; init; }

    /// <summary>On-disk / compressed size in bytes as reported by the source.</summary>
    public required long PhysicalFileSize { get; init; }

    /// <summary>UTC last-write timestamp reported by the source.</summary>
    public required DateTime LastWriteTime { get; init; }

    /// <summary>Absolute path where the exported artifact was written.</summary>
    public required string ExportPath { get; init; }

    /// <summary>Hex-encoded checksum of the exported payload (algorithm carried out-of-band).</summary>
    public required string Checksum { get; init; }

    /// <summary>Terminal status — <c>Succeeded</c> / <c>Failed</c> / <c>Skipped</c>.</summary>
    public required string ExportStatus { get; init; }

    /// <summary>UTC time the exporter observed this outcome.</summary>
    public required DateTime ExportDate { get; init; }

    /// <summary>Worker that produced the outcome.</summary>
    public required long WorkerId { get; init; }

    /// <summary>1-based attempt number that produced this outcome.</summary>
    public required int RetryCount { get; init; }

    /* ---- Optional EDMS-migration extensions ---- */

    /// <summary>
    /// Deterministic SHA-256 fingerprint over the source triple
    /// (part, version, dataFileVersion). Stable across processes; useful as
    /// a global unique key in the destination EDMS.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Source-schema identifier: <c>ID_DATAFILEVERSION</c>. Rarely needed downstream.</summary>
    public long? DataFileVersionId { get; init; }
}
