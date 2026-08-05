namespace MFilesExporter.Logging.Audit;

/// <summary>
/// Immutable audit record. Written to a dedicated append-only sink so it can
/// be shipped to a WORM store for compliance.
/// </summary>
/// <remarks>
/// Never place document payload or PII in <see cref="Data"/> — only surrogate
/// identifiers (document-file-part, version-part, data-file-version,
/// SHA-256 idempotency key hex).
/// </remarks>
public sealed record AuditEvent
{
    /// <summary>Timestamp when the audit event was raised (UTC).</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Verb / operation, e.g. <c>document.exported</c>, <c>job.started</c>, <c>work-claim.reclaimed</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Who / what triggered the action — worker id, user, service account.</summary>
    public required string Actor { get; init; }

    /// <summary>Resource acted upon, e.g. <c>document/DFV#123</c>, <c>job/42</c>.</summary>
    public required string Subject { get; init; }

    /// <summary>Terminal outcome — <c>success</c>, <c>failure</c>, <c>skipped</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Optional structured payload. Values must be JSON-serialisable primitives.</summary>
    public IReadOnlyDictionary<string, object?> Data { get; init; } = new Dictionary<string, object?>(0);

    /// <summary>Optional correlation id — if unset the writer fills it from the ambient accessor.</summary>
    public string? CorrelationId { get; init; }
}
