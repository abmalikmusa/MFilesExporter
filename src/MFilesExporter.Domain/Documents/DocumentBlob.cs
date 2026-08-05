namespace MFilesExporter.Domain.Documents;

/// <summary>
/// Domain-level representation of the binary payload of a document version.
/// This type does NOT hold the bytes; it holds the addressing information
/// and expected size envelope that lets an adapter stream the bytes on demand.
/// </summary>
/// <remarks>
/// The exporter is strict about never buffering BLOBs — infrastructure adapters
/// implement <c>IDocumentContentReader</c> which yields a forward-only stream
/// over the payload. <see cref="DocumentBlob"/> is what the domain hands to
/// that adapter and what the manifest records.
/// </remarks>
public sealed record DocumentBlob
{
    /// <param name="key">The BLOB's addressing key inside the source system.</param>
    /// <param name="declaredLogicalSize">
    /// Size the source system reports for this payload. The sink verifies that
    /// bytes-written equals this value; a mismatch is a data-integrity failure.
    /// </param>
    /// <param name="declaredContentType">
    /// Optional MIME type; <c>null</c> when the source does not know the type.
    /// Derived from the file extension when M-Files does not record one.
    /// </param>
    public DocumentBlob(
        DataFileVersionKey key,
        long declaredLogicalSize,
        string? declaredContentType = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(declaredLogicalSize);

        Key = key;
        DeclaredLogicalSize = declaredLogicalSize;
        DeclaredContentType = declaredContentType;
    }

    /// <summary>Composite key that uniquely identifies the payload in the source.</summary>
    public DataFileVersionKey Key { get; }

    /// <summary>Size the source system says the payload is, in bytes.</summary>
    public long DeclaredLogicalSize { get; }

    /// <summary>Best-guess MIME type; may be <c>null</c>.</summary>
    public string? DeclaredContentType { get; }

    /// <summary>True when the source claims a non-empty payload.</summary>
    public bool HasContent => DeclaredLogicalSize > 0;
}
