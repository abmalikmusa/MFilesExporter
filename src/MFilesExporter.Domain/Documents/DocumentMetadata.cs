namespace MFilesExporter.Domain.Documents;

/// <summary>
/// Immutable descriptive metadata for a single document file version.
/// Extracted from <see cref="DocumentDescriptor"/> so metadata can flow
/// independently through pipeline stages (e.g. a manifest writer may
/// serialize the metadata without holding the identifier keys).
/// </summary>
public sealed record DocumentMetadata
{
    /// <param name="title">Original file title, verbatim, as recorded by M-Files.</param>
    /// <param name="extension">File extension without a leading dot (e.g. "pdf"). May be empty.</param>
    /// <param name="logicalFileSize">Uncompressed size of the payload in bytes.</param>
    /// <param name="physicalFileSize">On-disk / compressed size in bytes.</param>
    /// <param name="lastWriteTimeUtc">UTC timestamp of the last write recorded by M-Files.</param>
    public DocumentMetadata(
        string title,
        string extension,
        long logicalFileSize,
        long physicalFileSize,
        DateTime lastWriteTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentOutOfRangeException.ThrowIfNegative(logicalFileSize);
        ArgumentOutOfRangeException.ThrowIfNegative(physicalFileSize);

        Title = title;
        Extension = extension ?? string.Empty;
        LogicalFileSize = logicalFileSize;
        PhysicalFileSize = physicalFileSize;
        LastWriteTimeUtc = DateTime.SpecifyKind(lastWriteTimeUtc, DateTimeKind.Utc);
    }

    /// <summary>
    /// Original file title, verbatim. Preserved for audit — do not sanitize here;
    /// path sanitation belongs in the sink.
    /// </summary>
    public string Title { get; }

    /// <summary>Extension without leading dot; empty when the source has no extension.</summary>
    public string Extension { get; }

    /// <summary>Logical (uncompressed) size in bytes; sink writes should match this.</summary>
    public long LogicalFileSize { get; }

    /// <summary>Physical (on-disk / compressed) size in bytes as recorded by M-Files.</summary>
    public long PhysicalFileSize { get; }

    /// <summary>Last-write timestamp; used for chronological reporting and audit.</summary>
    public DateTime LastWriteTimeUtc { get; }
}
