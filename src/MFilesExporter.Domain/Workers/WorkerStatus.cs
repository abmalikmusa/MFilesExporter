namespace MFilesExporter.Domain.Workers;

/// <summary>Lifecycle state of an <see cref="ExportWorker"/>.</summary>
public enum WorkerStatus
{
    /// <summary>Row inserted but the worker has not sent its first heartbeat.</summary>
    Registered = 0,

    /// <summary>Actively processing; heartbeat has arrived within the freshness window.</summary>
    Active = 1,

    /// <summary>No work to do but still alive.</summary>
    Idle = 2,

    /// <summary>Heartbeat exceeded the freshness window. Ops sweep marks these.</summary>
    Stalled = 3,

    /// <summary>Worker cleanly shut itself down.</summary>
    Stopped = 4,

    /// <summary>Worker terminated due to a fatal failure.</summary>
    Failed = 5,

    /// <summary>Archived along with its job.</summary>
    Archived = 6,
}
