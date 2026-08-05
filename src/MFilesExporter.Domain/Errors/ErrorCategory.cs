namespace MFilesExporter.Domain.Errors;

/// <summary>Category axis for an <see cref="ErrorRecord"/> — orthogonal to severity.</summary>
public enum ErrorCategory
{
    /// <summary>Retryable — deadlock, timeout, connection reset.</summary>
    Transient = 0,

    /// <summary>Not retryable — missing row, constraint violation, bad payload.</summary>
    Deterministic = 1,

    /// <summary>Configuration or wiring error — connection string wrong, missing table.</summary>
    Configuration = 2,

    /// <summary>Authentication or authorization failure.</summary>
    Security = 3,

    /// <summary>Storage-side failure — disk full, permission denied, IO exception.</summary>
    Storage = 4,

    /// <summary>Unclassified.</summary>
    Unknown = 5,
}
