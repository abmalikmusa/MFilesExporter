using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Application.Abstractions;

public interface IDocumentEnumerator
{
    /// <summary>Streams descriptors greater than the given cursor, ordered ascending.</summary>
    IAsyncEnumerable<DocumentDescriptor> EnumerateAsync(
        DocumentFileVersionKey exclusiveLowerBound,
        CancellationToken cancellationToken);

    /// <summary>Best-effort remaining-row count for ETA reporting only.</summary>
    Task<long> CountRemainingAsync(DocumentFileVersionKey exclusiveLowerBound, CancellationToken cancellationToken);
}
