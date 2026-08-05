namespace MFilesExporter.Application.Common;

/// <summary>
/// Structured description of a use-case failure. Modeled as a value type so
/// results can be composed and returned without allocating rich exceptions.
/// </summary>
/// <remarks>
/// <c>Code</c> is machine-readable; <c>Message</c> is human-readable. Reserve
/// <see cref="ApplicationErrorKind.Unexpected"/> for genuine bugs — every
/// other kind maps to a documented recovery path.
/// </remarks>
public sealed record ApplicationError(
    string Code,
    string Message,
    ApplicationErrorKind Kind = ApplicationErrorKind.Failure)
{
    /// <summary>Factory for validation errors from a single failed property.</summary>
    public static ApplicationError Validation(string code, string message) =>
        new(code, message, ApplicationErrorKind.Validation);

    /// <summary>Factory for "not found" errors — read paths returning nothing addressable.</summary>
    public static ApplicationError NotFound(string code, string message) =>
        new(code, message, ApplicationErrorKind.NotFound);

    /// <summary>Factory for authorization refusals.</summary>
    public static ApplicationError Forbidden(string code, string message) =>
        new(code, message, ApplicationErrorKind.Forbidden);

    /// <summary>Factory for state-machine violations — an operation illegal in current state.</summary>
    public static ApplicationError Conflict(string code, string message) =>
        new(code, message, ApplicationErrorKind.Conflict);

    /// <summary>Factory for transient/temporary failures a caller may retry.</summary>
    public static ApplicationError Transient(string code, string message) =>
        new(code, message, ApplicationErrorKind.Transient);

    /// <summary>Factory for unexpected errors — surface for bug tracking.</summary>
    public static ApplicationError Unexpected(string code, string message) =>
        new(code, message, ApplicationErrorKind.Unexpected);
}

/// <summary>Axis for the kind of failure — chosen so callers can react programmatically.</summary>
public enum ApplicationErrorKind
{
    /// <summary>Generic failure that does not fit another kind.</summary>
    Failure,
    /// <summary>Bad input — recoverable by fixing the request.</summary>
    Validation,
    /// <summary>Addressed entity does not exist.</summary>
    NotFound,
    /// <summary>Caller not permitted to perform the action.</summary>
    Forbidden,
    /// <summary>State does not permit the requested transition.</summary>
    Conflict,
    /// <summary>Temporary failure; retry with backoff.</summary>
    Transient,
    /// <summary>Bug — should not happen; log with high severity.</summary>
    Unexpected,
}
