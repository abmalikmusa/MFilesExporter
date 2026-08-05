using MFilesExporter.Application.Common;

namespace MFilesExporter.Application.Dispatching;

/// <summary>
/// Central invocation surface for the application layer. Consumers depend on
/// this abstraction rather than on individual handler types so cross-cutting
/// concerns (logging, correlation, metrics) can be added once, in one place.
/// </summary>
public interface IApplicationDispatcher
{
    Task<ApplicationResult> SendAsync<TCommand>(
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : ICommand;

    Task<ApplicationResult<TResult>> SendAsync<TCommand, TResult>(
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : ICommand<TResult>;

    Task<ApplicationResult<TResult>> QueryAsync<TQuery, TResult>(
        TQuery query,
        CancellationToken cancellationToken)
        where TQuery : IQuery<TResult>;
}
