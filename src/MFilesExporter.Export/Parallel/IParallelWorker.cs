namespace MFilesExporter.Export.Parallel;

/// <summary>
/// A worker handler. Implementations MUST be stateless (or thread-local)
/// because the engine invokes the same instance from multiple worker
/// tasks concurrently. Never throw for expected failures — surface them
/// through <see cref="WorkerContext"/> or via your own error-reporting
/// port. Exceptions escape to the engine and are logged.
/// </summary>
public interface IParallelWorker<in TItem>
{
    Task ProcessAsync(TItem item, WorkerContext context, CancellationToken cancellationToken);
}
