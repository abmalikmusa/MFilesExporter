namespace MFilesExporter.Export.Parallel;

/// <summary>Per-worker snapshot returned by <see cref="EngineStatus.Workers"/>.</summary>
public sealed record WorkerStatusSnapshot(
    int WorkerId,
    WorkerLiveness Liveness,
    long ItemsProcessed,
    DateTimeOffset LastHeartbeatUtc,
    TimeSpan HeartbeatAge);

/// <summary>Per-worker health label.</summary>
public enum WorkerLiveness
{
    /// <summary>Heartbeat within the freshness window.</summary>
    Healthy,
    /// <summary>Heartbeat overdue.</summary>
    Stalled,
    /// <summary>Worker task exited.</summary>
    Stopped,
}
