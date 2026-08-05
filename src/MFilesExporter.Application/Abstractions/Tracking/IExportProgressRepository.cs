using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.Abstractions.Tracking;

public interface IExportProgressRepository
{
    /// <summary>Appends a single progress snapshot. Prefer <see cref="RecordBatchAsync"/> for hot paths.</summary>
    Task RecordAsync(ExportProgressRecord snapshot, CancellationToken cancellationToken);

    /// <summary>Appends N snapshots in one server round-trip using a table-valued parameter.</summary>
    Task RecordBatchAsync(IReadOnlyCollection<ExportProgressRecord> snapshots, CancellationToken cancellationToken);

    /// <summary>Returns the most recent snapshot for the job or null.</summary>
    Task<ExportProgressRecord?> GetLatestAsync(long exportJobId, CancellationToken cancellationToken);
}
