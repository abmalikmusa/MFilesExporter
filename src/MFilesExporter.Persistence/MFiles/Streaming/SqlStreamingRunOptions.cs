namespace MFilesExporter.Persistence.MFiles.Streaming;

/// <summary>
/// Per-invocation overrides for the streaming engine. Any field left at its
/// default falls back to the corresponding <c>SqlStreamingOptions</c> value.
/// Defined as a mutable record with nullable overrides so callers can pass
/// only the settings they need.
/// </summary>
public sealed record SqlStreamingRunOptions
{
    /// <summary>Override the fetch size (rows per keyset-paginated round-trip).</summary>
    public int? FetchSize { get; init; }

    /// <summary>Override the metadata-command timeout.</summary>
    public TimeSpan? CommandTimeout { get; init; }

    /// <summary>Override the BLOB-command timeout.</summary>
    public TimeSpan? BlobCommandTimeout { get; init; }

    /// <summary>Override retry attempt count for a single failed operation.</summary>
    public int? MaxRetryAttempts { get; init; }

    /// <summary>Correlation string attached to all engine logs for this run.</summary>
    public string? CorrelationId { get; init; }
}
