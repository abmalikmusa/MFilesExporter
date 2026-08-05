using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.Abstractions.Tracking;

public interface IExportWorkerRepository
{
    Task<long> RegisterAsync(
        long exportJobId,
        string workerName,
        string machineName,
        int? processId,
        string assignedPartition,
        int concurrency,
        CancellationToken cancellationToken);

    Task HeartbeatAsync(long exportWorkerId, ExportWorkerStatus status, CancellationToken cancellationToken);

    Task StopAsync(long exportWorkerId, string? reason, CancellationToken cancellationToken);
}
