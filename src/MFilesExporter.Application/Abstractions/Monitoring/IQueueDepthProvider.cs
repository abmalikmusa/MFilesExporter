namespace MFilesExporter.Application.Abstractions.Monitoring;

/// <summary>
/// Contract for any channel / queue / buffer that wants its depth published
/// as a monitoring gauge. Implementations register themselves at DI time
/// and the telemetry adapter samples them on every OpenTelemetry export tick.
/// </summary>
public interface IQueueDepthProvider
{
    /// <summary>Logical name of the queue — becomes the <c>queue</c> tag on the gauge.</summary>
    string Name { get; }

    /// <summary>Current number of items buffered.</summary>
    int Depth { get; }

    /// <summary>Configured capacity, or <c>null</c> when unbounded.</summary>
    int? Capacity { get; }
}

/// <summary>Utilization signal exposed by the parallel processing engine.</summary>
public interface IWorkerUtilizationProvider
{
    /// <summary>Configured worker count (upper bound).</summary>
    int WorkerCount { get; }

    /// <summary>Number of workers currently executing a work item.</summary>
    int BusyWorkers { get; }

    /// <summary>Number of workers flagged as stalled (no heartbeat within threshold).</summary>
    int StalledWorkers { get; }
}

/// <summary>Progress signal used by the ETA gauge.</summary>
public interface IProgressSnapshotProvider
{
    /// <summary>Documents completed so far (Succeeded + Failed + Skipped).</summary>
    long TotalRecorded { get; }

    /// <summary>Total expected documents. <c>0</c> when the target is unknown.</summary>
    long TotalExpected { get; }

    /// <summary>UTC start time of the current job.</summary>
    DateTimeOffset? StartedAtUtc { get; }
}
