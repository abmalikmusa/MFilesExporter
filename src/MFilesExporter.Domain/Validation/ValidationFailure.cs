namespace MFilesExporter.Domain.Validation;

/// <summary>
/// A single validation error emitted by an aggregate's <c>Validate()</c>.
/// Immutable, JSON-serializable, and precise enough to surface as an
/// individual API problem-details entry.
/// </summary>
/// <param name="PropertyName">
/// Dot-notated path of the failing property (e.g. <c>Configuration.BatchSize</c>).
/// Never null; may be empty for aggregate-level failures.
/// </param>
/// <param name="ErrorCode">
/// Machine-readable stable identifier for the failure kind. Used by
/// programmatic consumers to react to specific failures without parsing
/// human-readable messages.
/// </param>
/// <param name="Message">Human-readable message suitable for logging and UI.</param>
public sealed record ValidationFailure(
    string PropertyName,
    string ErrorCode,
    string Message);
