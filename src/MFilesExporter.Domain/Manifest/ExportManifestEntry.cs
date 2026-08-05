using MFilesExporter.Domain.Documents;
using MFilesExporter.Domain.Results;

namespace MFilesExporter.Domain.Manifest;

/// <summary>
/// A single row in the export manifest — one per terminal outcome. Manifest
/// entries are immutable and JSON-serializable; they are the audit source of
/// truth even after the tracking DB is archived.
/// </summary>
public sealed record ExportManifestEntry
{
    /// <summary>Deterministic idempotency key for the source triple.</summary>
    public required IdempotencyKey IdempotencyKey { get; init; }

    /// <summary>Source part id.</summary>
    public required long DocumentFilePartId { get; init; }

    /// <summary>Source version part id.</summary>
    public required long VersionPartId { get; init; }

    /// <summary>Source data-file-version id.</summary>
    public required long DataFileVersionId { get; init; }

    /// <summary>Original file title from metadata.</summary>
    public required string Title { get; init; }

    /// <summary>Extension (no leading dot).</summary>
    public required string Extension { get; init; }

    /// <summary>Logical size claimed by the source.</summary>
    public required long DeclaredLogicalSize { get; init; }

    /// <summary>Terminal state.</summary>
    public required ExportStatus Status { get; init; }

    /// <summary>Bytes written; 0 unless Succeeded.</summary>
    public required long BytesWritten { get; init; }

    /// <summary>Final output path.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Hex-encoded SHA-256 of the written payload.</summary>
    public string? Checksum { get; init; }

    /// <summary>Failure reason for Failed / Skipped.</summary>
    public string? FailureReason { get; init; }

    /// <summary>UTC observation time.</summary>
    public required DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>Attempt number that produced this outcome.</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>Projects an ExportResult + descriptor into a manifest entry.</summary>
    public static ExportManifestEntry From(ExportResult result, DocumentMetadata metadata) => new()
    {
        IdempotencyKey = result.IdempotencyKey,
        DocumentFilePartId = result.DocumentFileVersionKey.DocumentFilePartId,
        VersionPartId = result.DocumentFileVersionKey.VersionPartId,
        DataFileVersionId = result.DataFileVersionKey.DataFileVersionId,
        Title = metadata.Title,
        Extension = metadata.Extension,
        DeclaredLogicalSize = metadata.LogicalFileSize,
        Status = result.Status,
        BytesWritten = result.BytesWritten,
        OutputPath = result.OutputPath,
        Checksum = result.PayloadChecksum,
        FailureReason = result.FailureReason,
        ObservedAtUtc = result.ObservedAtUtc,
        AttemptNumber = result.AttemptNumber,
    };
}
