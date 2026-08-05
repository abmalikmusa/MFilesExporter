using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.Abstractions.Tracking;

public interface IExportAuditRepository
{
    /// <summary>
    /// Read the last N audit rows for a job, most recent first. Used by
    /// dashboards and forensics tooling.
    /// </summary>
    IAsyncEnumerable<ExportAuditRecord> ReadRecentAsync(
        long exportJobId,
        int take,
        CancellationToken cancellationToken);
}
