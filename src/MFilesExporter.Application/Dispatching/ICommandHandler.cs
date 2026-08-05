using MFilesExporter.Application.Common;

namespace MFilesExporter.Application.Dispatching;

/// <summary>Handles a command whose success carries no payload.</summary>
public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    Task<ApplicationResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

/// <summary>Handles a command whose success carries a typed payload.</summary>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<ApplicationResult<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
