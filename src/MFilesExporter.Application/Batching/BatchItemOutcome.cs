namespace MFilesExporter.Application.Batching;

/// <summary>Terminal outcome of a single item within a batch.</summary>
public enum BatchItemOutcome
{
    /// <summary>Item processed successfully and its side effects are durable.</summary>
    Succeeded = 0,

    /// <summary>Item failed (permanent or retry-exhausted).</summary>
    Failed = 1,

    /// <summary>Item deliberately skipped — e.g. stale claim token, missing source content.</summary>
    Skipped = 2,
}
