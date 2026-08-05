using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.UseCases.Errors;

/// <summary>
/// Read the audit trail for a job. Used by dashboards and forensic tooling.
/// Returns entries most-recent-first.
/// </summary>
public sealed record GetRecentAuditQuery(long ExportJobId, int Take) : IQuery<IReadOnlyList<ExportAuditRecord>>;

public sealed class GetRecentAuditHandler : IQueryHandler<GetRecentAuditQuery, IReadOnlyList<ExportAuditRecord>>
{
    private readonly IExportAuditRepository _audit;

    public GetRecentAuditHandler(IExportAuditRepository audit)
    {
        _audit = audit;
    }

    public async Task<ApplicationResult<IReadOnlyList<ExportAuditRecord>>> HandleAsync(
        GetRecentAuditQuery query,
        CancellationToken cancellationToken)
    {
        if (query.ExportJobId <= 0)
            return ApplicationResult<IReadOnlyList<ExportAuditRecord>>.Failure(
                ApplicationError.Validation("JOB_ID_REQUIRED", "ExportJobId must be positive."));
        if (query.Take is <= 0 or > 10_000)
            return ApplicationResult<IReadOnlyList<ExportAuditRecord>>.Failure(
                ApplicationError.Validation("TAKE_RANGE", "Take must be between 1 and 10 000."));

        var buffer = new List<ExportAuditRecord>(Math.Min(query.Take, 1024));
        await foreach (var row in _audit.ReadRecentAsync(query.ExportJobId, query.Take, cancellationToken)
            .ConfigureAwait(false))
        {
            buffer.Add(row);
        }
        return ApplicationResult<IReadOnlyList<ExportAuditRecord>>.Success(buffer);
    }
}
