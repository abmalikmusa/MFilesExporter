using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.Abstractions.Tracking;

public interface IExportCheckpointRepository
{
    /// <summary>
    /// Monotonic upsert. Returns true if the checkpoint advanced, false if the
    /// candidate was not strictly greater than the current Active row.
    /// </summary>
    Task<bool> SaveAsync(
        long exportJobId,
        string partitionKey,
        long lastDocumentFilePartId,
        long lastVersionPartId,
        long? documentsProcessedInPartition,
        CancellationToken cancellationToken);

    Task<ExportCheckpointRecord?> GetActiveAsync(
        long exportJobId,
        string partitionKey,
        CancellationToken cancellationToken);
}
