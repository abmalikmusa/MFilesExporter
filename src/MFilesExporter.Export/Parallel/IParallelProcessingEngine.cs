using System.Threading.Channels;

namespace MFilesExporter.Export.Parallel;

/// <summary>
/// A hosted, pause-able, health-monitored producer/consumer engine.
///
/// Producers write items via <see cref="Writer"/>; a configurable pool of
/// worker tasks consume items via <see cref="IParallelWorker{TItem}"/>.
/// Shutdown is graceful — producers complete the writer, workers drain
/// the channel, the engine transitions to <see cref="EngineState.Stopped"/>.
/// </summary>
public interface IParallelProcessingEngine<TItem>
{
    /// <summary>Current lifecycle state.</summary>
    EngineState State { get; }

    /// <summary>Producer-side handle. Complete it (or call <see cref="StopAsync"/>) to signal end-of-work.</summary>
    ChannelWriter<TItem> Writer { get; }

    /// <summary>Point-in-time diagnostics snapshot.</summary>
    EngineStatus GetStatus();

    /// <summary>Async stream of every worker heartbeat.</summary>
    IAsyncEnumerable<WorkerHeartbeatEvent> Heartbeats { get; }

    /// <summary>Start the worker pool. Idempotent; subsequent calls are ignored.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Pause the workers. In-flight items complete; new items are not picked up until <see cref="ResumeAsync"/>.</summary>
    Task PauseAsync(CancellationToken cancellationToken);

    /// <summary>Resume paused workers.</summary>
    Task ResumeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Signal graceful shutdown: complete the writer, wait for in-flight
    /// work to drain (up to <c>ParallelProcessingOptions.GracefulShutdownTimeout</c>),
    /// then cancel remaining workers.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken);
}
