namespace MFilesExporter.Export.Parallel;

/// <summary>Lifecycle state of the parallel processing engine.</summary>
public enum EngineState
{
    /// <summary>Not yet started.</summary>
    NotStarted = 0,

    /// <summary>Workers are running and accepting items.</summary>
    Running = 1,

    /// <summary>Workers are paused between items.</summary>
    Paused = 2,

    /// <summary>Input channel closed; workers draining in-flight work.</summary>
    ShuttingDown = 3,

    /// <summary>Workers have exited.</summary>
    Stopped = 4,

    /// <summary>Terminated by an unhandled error.</summary>
    Faulted = 5,
}
