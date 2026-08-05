using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.Abstractions.Tracking;

public interface IExportErrorRepository
{
    Task<long> LogAsync(ExportErrorRecord error, CancellationToken cancellationToken);

    Task LogBatchAsync(IReadOnlyCollection<ExportErrorRecord> errors, CancellationToken cancellationToken);

    Task ResolveAsync(
        long exportErrorId,
        ExportErrorStatus newStatus,
        string? notes,
        string? actorName,
        CancellationToken cancellationToken);
}
