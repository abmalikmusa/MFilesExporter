namespace MFilesExporter.Export.Parallel;

/// <summary>
/// Ambient context passed to every worker invocation. Immutable within a
/// worker's lifetime; parallel workers each have their own instance.
/// </summary>
public sealed record WorkerContext
{
    /// <summary>0-based worker index within the pool.</summary>
    public required int WorkerId { get; init; }

    /// <summary>Human-readable pool name used in log correlation.</summary>
    public required string PoolName { get; init; }

    /// <summary>UTC time this worker started.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// Items successfully processed by THIS worker so far. Advanced by the
    /// engine after each successful invocation of the worker's handler.
    /// </summary>
    public long ItemsProcessed { get; internal set; }
}
