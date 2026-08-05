using MFilesExporter.Application.Abstractions;
using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Persistence.MFiles.Streaming;

/// <summary>
/// Yielded by <see cref="ISqlStreamingEngine"/> for every metadata row.
/// Composes:
/// <list type="bullet">
///   <item><description><see cref="DocumentDescriptor"/> — the strongly-typed metadata snapshot.</description></item>
///   <item><description><see cref="OpenContentStreamAsync"/> — deferred BLOB fetch (never eagerly opened).</description></item>
/// </list>
/// Callers control when (and whether) to fetch the BLOB. This lets downstream
/// stages parallelize BLOB fetches independently from the metadata cursor.
/// </summary>
public sealed class StreamedDocumentDescriptor
{
    private readonly Func<CancellationToken, Task<DocumentContentStream>> _openContent;

    internal StreamedDocumentDescriptor(
        DocumentDescriptor descriptor,
        Func<CancellationToken, Task<DocumentContentStream>> openContent)
    {
        Descriptor = descriptor;
        _openContent = openContent;
    }

    /// <summary>Immutable metadata snapshot.</summary>
    public DocumentDescriptor Descriptor { get; }

    /// <summary>
    /// Opens a streaming BLOB reader for this document. The returned
    /// <see cref="DocumentContentStream"/> owns a fresh <c>SqlConnection</c>
    /// and <c>SqlDataReader</c>; the caller MUST dispose it. Never buffers
    /// the payload in memory — uses <see cref="Microsoft.Data.SqlClient.SqlDataReader.GetBytes"/>
    /// in chunks under <see cref="System.Data.CommandBehavior.SequentialAccess"/>.
    /// </summary>
    public Task<DocumentContentStream> OpenContentStreamAsync(CancellationToken cancellationToken) =>
        _openContent(cancellationToken);
}
