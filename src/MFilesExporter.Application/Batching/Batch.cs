namespace MFilesExporter.Application.Batching;

/// <summary>
/// One unit of work processed together. The engine fetches, processes, and
/// completes one batch before advancing to the next. Never holds more than
/// one batch in memory at a time.
/// </summary>
public sealed record Batch<T>
{
    /// <summary>Monotonic 1-based batch number within a run — used for logs and progress.</summary>
    public required long BatchNumber { get; init; }

    /// <summary>
    /// Immutable slice of items to process. The list is never copied by the
    /// engine — it is passed by reference to the executor.
    /// </summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>UTC time the batch was fetched from the source.</summary>
    public required DateTimeOffset FetchedAtUtc { get; init; }

    /// <summary>Convenience.</summary>
    public int Size => Items.Count;

    /// <summary>True when the batch has no items (used to detect end-of-source).</summary>
    public bool IsEmpty => Items.Count == 0;
}
