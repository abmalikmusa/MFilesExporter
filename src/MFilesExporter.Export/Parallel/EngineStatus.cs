namespace MFilesExporter.Export.Parallel;

/// <summary>
/// Point-in-time snapshot of the engine. Callers use this to build
/// dashboards, health probes, or metric emitters.
/// </summary>
public sealed record EngineStatus(
    EngineState State,
    int WorkerCount,
    int ItemsInChannel,
    long TotalItemsProcessed,
    long TotalItemsFailed,
    IReadOnlyList<WorkerStatusSnapshot> Workers,
    DateTimeOffset ObservedAtUtc);
