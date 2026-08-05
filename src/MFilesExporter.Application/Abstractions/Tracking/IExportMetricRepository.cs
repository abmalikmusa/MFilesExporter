using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.Abstractions.Tracking;

public interface IExportMetricRepository
{
    Task RecordAsync(ExportMetricRecord metric, CancellationToken cancellationToken);

    /// <summary>
    /// Streams N metric rows to the DB in one round-trip. Prefer this over
    /// <see cref="RecordAsync"/> for anything above a handful of samples.
    /// </summary>
    Task RecordBatchAsync(IReadOnlyCollection<ExportMetricRecord> metrics, CancellationToken cancellationToken);
}
