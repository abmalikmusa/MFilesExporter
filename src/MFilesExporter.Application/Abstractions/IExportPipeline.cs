namespace MFilesExporter.Application.Abstractions;

/// <summary>
/// The pipeline is owned and implemented in MFilesExporter.Export.
/// The Application layer only depends on this port so the orchestrator
/// remains free of streaming/channel details.
/// </summary>
public interface IExportPipeline
{
    Task RunAsync(CancellationToken cancellationToken);
}
