using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Application.Abstractions;

public interface IExportStateStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<DocumentFileVersionKey> GetCheckpointAsync(string partitionKey, CancellationToken cancellationToken);
    Task SaveCheckpointAsync(string partitionKey, DocumentFileVersionKey checkpoint, CancellationToken cancellationToken);

    Task RecordOutcomeAsync(ExportOutcome outcome, CancellationToken cancellationToken);
    Task RecordOutcomesAsync(IReadOnlyCollection<ExportOutcome> outcomes, CancellationToken cancellationToken);

    Task<ExportStatus> GetStatusAsync(IdempotencyKey key, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<IdempotencyKey, ExportStatus>> GetStatusesAsync(
        IReadOnlyCollection<IdempotencyKey> keys,
        CancellationToken cancellationToken);

    Task<StateStoreCounters> GetCountersAsync(CancellationToken cancellationToken);
}

public sealed record StateStoreCounters(
    long TotalRecorded,
    long TotalSucceeded,
    long TotalFailed,
    long TotalSkipped,
    long TotalBytesWritten);
