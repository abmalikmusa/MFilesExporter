using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.Models.Tracking;

namespace MFilesExporter.Application.UseCases.Jobs;

/// <summary>Convenience wrapper around <see cref="CompleteExportJobCommand"/> for cancellation.</summary>
public sealed record CancelExportJobCommand : ICommand
{
    public required long ExportJobId { get; init; }
    public string? Reason { get; init; }
}

public sealed class CancelExportJobHandler : ICommandHandler<CancelExportJobCommand>
{
    private readonly IApplicationDispatcher _dispatcher;

    public CancelExportJobHandler(IApplicationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public Task<ApplicationResult> HandleAsync(
        CancelExportJobCommand command,
        CancellationToken cancellationToken) =>
        _dispatcher.SendAsync(new CompleteExportJobCommand
        {
            ExportJobId    = command.ExportJobId,
            TerminalStatus = ExportJobStatus.Cancelled,
            Reason         = command.Reason,
        }, cancellationToken);
}
