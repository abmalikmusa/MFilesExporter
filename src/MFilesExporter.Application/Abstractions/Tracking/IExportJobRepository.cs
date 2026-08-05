using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.Abstractions.Tracking;

public interface IExportJobRepository
{
    /// <summary>Creates a new job in the tracking DB and marks it Running.</summary>
    Task<long> StartAsync(
        string jobName,
        string sourceServer,
        string sourceDatabase,
        string partitionKey,
        long? totalDocumentsExpected,
        CancellationToken cancellationToken);

    /// <summary>Marks a job as Completed, Failed, or Cancelled.</summary>
    Task CompleteAsync(
        long exportJobId,
        ExportJobStatus terminalStatus,
        string? reason,
        CancellationToken cancellationToken);

    Task<ExportJobRecord?> GetAsync(long exportJobId, CancellationToken cancellationToken);
}
