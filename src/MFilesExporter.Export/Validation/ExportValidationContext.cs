using MFilesExporter.Domain.Documents;
using MFilesExporter.Export.Metadata;

namespace MFilesExporter.Export.Validation;

/// <summary>
/// Everything a validator needs to inspect one exported document.
/// Immutable — safe to share across parallel validators. Callers populate
/// only the fields relevant to their validation set; unused fields are
/// null-safe.
/// </summary>
public sealed record ExportValidationContext
{
    /// <summary>The domain descriptor from which the export was derived.</summary>
    public required DocumentDescriptor Descriptor { get; init; }

    /// <summary>Absolute path of the artifact that was just written.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Expected total bytes written. Compared to <c>FileInfo.Length</c>.</summary>
    public required long ExpectedByteCount { get; init; }

    /// <summary>Hex-encoded expected checksum (typically SHA-256).</summary>
    public required string ExpectedChecksumHex { get; init; }

    /// <summary>Expected file extension without leading dot; may be empty.</summary>
    public required string ExpectedExtension { get; init; }

    /// <summary>Root directory beneath which every valid output path must live.</summary>
    public required string ExpectedRootDirectory { get; init; }

    /// <summary>
    /// Optional metadata record emitted for this document. When supplied,
    /// the metadata-consistency validator cross-references it against the
    /// on-disk file.
    /// </summary>
    public MetadataRecord? MetadataRecord { get; init; }

    /// <summary>Optional correlation identifier for cross-log stitching.</summary>
    public string? CorrelationId { get; init; }
}
