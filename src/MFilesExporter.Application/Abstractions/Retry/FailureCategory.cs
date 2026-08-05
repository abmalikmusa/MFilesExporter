namespace MFilesExporter.Application.Abstractions.Retry;

/// <summary>
/// Canonical taxonomy of failure modes recognised by the retry engine.
/// Every classified <see cref="Exception"/> maps to exactly one category,
/// which in turn drives retry, back-off, and circuit-breaker behaviour.
/// </summary>
/// <remarks>
/// The list is intentionally coarse-grained: it exists to control policy
/// dispatch, not to describe root cause. Two SQL errors that share the same
/// remediation collapse to the same category.
/// </remarks>
public enum FailureCategory
{
    /// <summary>Unclassified — the executor treats it as permanent.</summary>
    Unknown = 0,

    /// <summary>SQL command exceeded its execution timeout (server-side or client-side).</summary>
    SqlTimeout,

    /// <summary>SQL Server chose this session as a deadlock victim (error 1205) or lock request timed out (1222).</summary>
    SqlDeadlock,

    /// <summary>Any other transient SQL error (throttling, node failover, connection reset by peer).</summary>
    SqlTransient,

    /// <summary>TCP/socket-level fault: connection reset, socket exception, name resolution, DNS.</summary>
    NetworkInterruption,

    /// <summary>Generic I/O fault reading from or writing to a local or remote file system.</summary>
    IoFailure,

    /// <summary>Storage is full (ERROR_DISK_FULL / ENOSPC). Retry is only useful if another process frees space.</summary>
    DiskFull,

    /// <summary>Filesystem or database permissions denied the operation. Permanent by default.</summary>
    PermissionDenied,

    /// <summary>Downstream deliberately throttled the request (HTTP 429, SQL 40501, throttling exceptions).</summary>
    RateLimited,

    /// <summary>Operation was cancelled by the caller. Never retried.</summary>
    Cancelled,

    /// <summary>Deterministic bug in the caller — validation, argument, invariant. Never retried.</summary>
    Permanent,
}

/// <summary>Extension helpers over <see cref="FailureCategory"/>.</summary>
public static class FailureCategoryExtensions
{
    /// <summary>Returns true for categories the executor is allowed to retry.</summary>
    public static bool IsRetryable(this FailureCategory category) => category switch
    {
        FailureCategory.SqlTimeout          => true,
        FailureCategory.SqlDeadlock         => true,
        FailureCategory.SqlTransient        => true,
        FailureCategory.NetworkInterruption => true,
        FailureCategory.IoFailure           => true,
        FailureCategory.DiskFull            => true,   // limited attempts, see policy
        FailureCategory.RateLimited         => true,
        _                                   => false,
    };
}
