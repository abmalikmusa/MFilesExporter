namespace MFilesExporter.Domain.Documents;

/// <summary>
/// The unit of work that flows through the export pipeline.
///
/// A <see cref="DocumentDescriptor"/> is a compact, immutable pointer to a
/// single document file version in the source vault. It composes:
/// <list type="bullet">
///   <item><description><see cref="DocumentFileVersionKey"/> — the enumeration cursor.</description></item>
///   <item><description><see cref="DocumentBlob"/> — the payload addressing key.</description></item>
///   <item><description><see cref="DocumentMetadata"/> — the descriptive attributes.</description></item>
///   <item><description><see cref="IdempotencyKey"/> — a deterministic identity for the tuple, computed once.</description></item>
/// </list>
///
/// Legacy convenience accessors (<c>Title</c>, <c>Extension</c>, ...) forward to
/// <see cref="Metadata"/> so pipeline code that predates this refactor still
/// compiles without changes.
/// </summary>
public sealed record DocumentDescriptor
{
    /// <summary>Full-fidelity constructor. Composes metadata + blob explicitly.</summary>
    public DocumentDescriptor(
        DocumentFileVersionKey documentFileVersionKey,
        DocumentBlob blob,
        DocumentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentNullException.ThrowIfNull(metadata);

        if (documentFileVersionKey.DocumentFilePartId != blob.Key.DocumentFilePartId)
        {
            throw new ArgumentException(
                "DocumentFileVersionKey.DocumentFilePartId must match Blob.Key.DocumentFilePartId "
                + "(they are the same column in the source schema).",
                nameof(blob));
        }

        DocumentFileVersionKey = documentFileVersionKey;
        Blob = blob;
        Metadata = metadata;
        IdempotencyKey = IdempotencyKey.For(
            documentFileVersionKey.DocumentFilePartId,
            documentFileVersionKey.VersionPartId,
            blob.Key.DataFileVersionId);
    }

    /// <summary>
    /// Backward-compatible constructor accepting the flat set of fields used
    /// by older call-sites (the enumeration reader). New callers should prefer
    /// the compositional constructor above.
    /// </summary>
    public DocumentDescriptor(
        DocumentFileVersionKey documentFileVersionKey,
        DataFileVersionKey dataFileVersionKey,
        string title,
        string extension,
        long logicalFileSize,
        long physicalFileSize,
        DateTime lastWriteTimeUtc)
        : this(
            documentFileVersionKey,
            new DocumentBlob(dataFileVersionKey, logicalFileSize),
            new DocumentMetadata(title, extension, logicalFileSize, physicalFileSize, lastWriteTimeUtc))
    {
    }

    /// <summary>Enumeration cursor for this descriptor. Ordered pagination key.</summary>
    public DocumentFileVersionKey DocumentFileVersionKey { get; }

    /// <summary>Address + envelope for the binary payload.</summary>
    public DocumentBlob Blob { get; }

    /// <summary>Descriptive metadata for reporting and manifest.</summary>
    public DocumentMetadata Metadata { get; }

    /// <summary>Deterministic idempotency key for this (part, ver, dataFileVer) triple.</summary>
    public IdempotencyKey IdempotencyKey { get; }

    // Convenience accessors — forward to Blob / Metadata.

    /// <summary>Convenience: the composite key of the underlying BLOB row.</summary>
    public DataFileVersionKey DataFileVersionKey => Blob.Key;

    /// <summary>Convenience: original document title from metadata.</summary>
    public string Title => Metadata.Title;

    /// <summary>Convenience: extension (no leading dot).</summary>
    public string Extension => Metadata.Extension;

    /// <summary>Convenience: logical (uncompressed) size in bytes.</summary>
    public long LogicalFileSize => Metadata.LogicalFileSize;

    /// <summary>Convenience: physical (on-disk / compressed) size in bytes.</summary>
    public long PhysicalFileSize => Metadata.PhysicalFileSize;

    /// <summary>Convenience: last-write timestamp (UTC).</summary>
    public DateTime LastWriteTimeUtc => Metadata.LastWriteTimeUtc;
}
