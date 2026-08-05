namespace MFilesExporter.Configuration.Options;

/// <summary>
/// Configuration for the generic parallel processing engine (based on
/// <c>System.Threading.Channels</c>, worker pool, and async streams).
/// One instance per hosted engine.
/// </summary>
public sealed class ParallelProcessingOptions
{
    public const string SectionName = "Exporter:ParallelProcessing";

    /// <summary>Number of concurrent worker tasks reading from the input channel.</summary>
    public int WorkerCount { get; set; } = 8;

    /// <summary>
    /// Capacity of the bounded input channel. Larger = more back-pressure
    /// slack for burst producers, at the cost of more items buffered in
    /// memory. Set to a small multiple of <see cref="WorkerCount"/>.
    /// </summary>
    public int ChannelCapacity { get; set; } = 128;

    /// <summary>
    /// Behaviour when the input channel is full. <c>Wait</c> back-pressures
    /// the producer (recommended); <c>DropOldest</c> / <c>DropNewest</c>
    /// discard items silently.
    /// </summary>
    public ChannelFullMode FullMode { get; set; } = ChannelFullMode.Wait;

    /// <summary>Heartbeat cadence emitted by every worker while idle or busy.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Workers whose last heartbeat is older than this are flagged Stalled.</summary>
    public TimeSpan StalledThreshold { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Time to wait for in-flight work to drain during graceful shutdown.
    /// After this, remaining workers are cancelled.
    /// </summary>
    public TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Restart a worker task if it exits due to an unhandled exception
    /// (rather than letting the pool shrink silently). Off by default —
    /// unhandled exceptions usually indicate a bug in the worker handler.
    /// </summary>
    public bool RestartWorkersOnFault { get; set; }
}

/// <summary>How the engine behaves when the input channel is full.</summary>
public enum ChannelFullMode
{
    /// <summary>Producer awaits until space is available (back-pressure).</summary>
    Wait,
    /// <summary>Drop the oldest queued item.</summary>
    DropOldest,
    /// <summary>Drop the incoming item.</summary>
    DropNewest,
}
