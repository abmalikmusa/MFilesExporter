namespace MFilesExporter.Export.Parallel;

/// <summary>
/// A single heartbeat emitted by a worker. Consumed by
/// <see cref="WorkerHealthMonitor"/> and exposed to observers as an async
/// stream via <c>IParallelProcessingEngine.Heartbeats</c>.
/// </summary>
public sealed record WorkerHeartbeatEvent(
    int WorkerId,
    string PoolName,
    WorkerHeartbeatKind Kind,
    DateTimeOffset EmittedAtUtc,
    long ItemsProcessedTotal,
    long ItemsFailedTotal);

/// <summary>Kind axis for heartbeat events.</summary>
public enum WorkerHeartbeatKind
{
    /// <summary>Periodic timer beat while idle (no item in flight).</summary>
    Idle,
    /// <summary>Emitted after successfully processing one item.</summary>
    Processed,
    /// <summary>Emitted after a handler exception.</summary>
    Failed,
    /// <summary>Emitted when the worker enters the paused wait.</summary>
    Paused,
    /// <summary>Emitted when the worker exits normally.</summary>
    Stopped,
}
