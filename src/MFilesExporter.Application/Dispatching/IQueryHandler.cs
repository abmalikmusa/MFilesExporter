using MFilesExporter.Application.Common;

namespace MFilesExporter.Application.Dispatching;

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<ApplicationResult<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
