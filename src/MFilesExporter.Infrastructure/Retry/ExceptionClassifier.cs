using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using MFilesExporter.Application.Abstractions.Retry;
using Microsoft.Data.SqlClient;

namespace MFilesExporter.Infrastructure.Retry;

/// <summary>
/// Default <see cref="IFailureClassifier"/>. Recognises SQL Server error
/// numbers, Winsock/BSD socket codes, Win32 <c>HResult</c> masks, and
/// well-known CLR exception types.
/// </summary>
/// <remarks>
/// The classifier is stateless and thread-safe. It never allocates on the
/// hot path — all decisions are integer comparisons or type checks.
/// <para/>
/// SQL error numbers are taken from Microsoft's official transient-error
/// list: connection-reset (-2, 10053, 10054, 10060), throttling
/// (40197, 40501, 40613, 49918-49920), deadlock (1205) and lock timeout
/// (1222). See <c>docs/retry-handling.md</c> for the exhaustive map.
/// </remarks>
public sealed class ExceptionClassifier : IFailureClassifier
{
    // Win32 HResult codes reachable from IOException.HResult on Windows.
    private const int HResultErrorDiskFull      = unchecked((int)0x80070070);   // ERROR_DISK_FULL
    private const int HResultErrorHandleDiskFull = unchecked((int)0x80070027);  // ERROR_HANDLE_DISK_FULL
    private const int HResultErrorAccessDenied  = unchecked((int)0x80070005);   // ERROR_ACCESS_DENIED
    private const int HResultErrorSharingViolation = unchecked((int)0x80070020);
    private const int HResultErrorLockViolation = unchecked((int)0x80070021);

    // POSIX errno values reachable from IOException.HResult on Linux/macOS.
    private const int PosixEnospc  = 28;   // No space left on device
    private const int PosixEdquot  = 122;  // Disk quota exceeded (Linux)
    private const int PosixEacces  = 13;   // Permission denied
    private const int PosixEperm   = 1;    // Operation not permitted

    public FailureCategory Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Cancellation always wins — never treated as retryable.
        if (exception is OperationCanceledException) return FailureCategory.Cancelled;

        // Unwrap AggregateException (typical for Task.WhenAll / Parallel workflows).
        if (exception is AggregateException agg && agg.InnerException is not null)
            return Classify(agg.InnerException);

        return exception switch
        {
            SqlException sql              => ClassifySql(sql),
            TimeoutException              => FailureCategory.SqlTimeout,
            SocketException socket        => ClassifySocket(socket),
            UnauthorizedAccessException   => FailureCategory.PermissionDenied,
            SecurityException             => FailureCategory.PermissionDenied,
            IOException io                => ClassifyIo(io),
            Win32Exception w32            => ClassifyWin32(w32),
            ArgumentException             => FailureCategory.Permanent,
            InvalidOperationException     => FailureCategory.Permanent,
            NotSupportedException         => FailureCategory.Permanent,
            _                             => FailureCategory.Unknown,
        };
    }

    private static FailureCategory ClassifySql(SqlException ex)
    {
        // SqlException.Number reports the first error; iterate for the most-specific.
        foreach (SqlError err in ex.Errors)
        {
            var category = MapSqlNumber(err.Number);
            if (category != FailureCategory.Unknown) return category;
        }

        return MapSqlNumber(ex.Number);
    }

    private static FailureCategory MapSqlNumber(int number) => number switch
    {
        // Deadlock and lock timeout — retry aggressively, no CB.
        1205 => FailureCategory.SqlDeadlock,
        1222 => FailureCategory.SqlDeadlock,

        // Client-side timeout signalled as SQL error -2.
        -2   => FailureCategory.SqlTimeout,

        // Connection-level transients.
        20     or 64    or 233   or 232          => FailureCategory.NetworkInterruption,
        10053  or 10054 or 10060 or 10061        => FailureCategory.NetworkInterruption,
        11001  or 258                            => FailureCategory.NetworkInterruption,
        121    or 615   or 4060  or 4221         => FailureCategory.SqlTransient,

        // Azure SQL / throttling.
        40197 or 40501 or 40613
              or 49918 or 49919 or 49920         => FailureCategory.RateLimited,

        // Permissions & authorisation.
        18456 or 18452 or 916   or 229           => FailureCategory.PermissionDenied,

        _ => FailureCategory.Unknown,
    };

    private static FailureCategory ClassifySocket(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.TimedOut          => FailureCategory.SqlTimeout,
        SocketError.HostNotFound      => FailureCategory.NetworkInterruption,
        SocketError.TryAgain          => FailureCategory.NetworkInterruption,
        SocketError.ConnectionReset   => FailureCategory.NetworkInterruption,
        SocketError.ConnectionAborted => FailureCategory.NetworkInterruption,
        SocketError.NetworkDown       => FailureCategory.NetworkInterruption,
        SocketError.NetworkReset      => FailureCategory.NetworkInterruption,
        SocketError.NetworkUnreachable=> FailureCategory.NetworkInterruption,
        SocketError.HostDown          => FailureCategory.NetworkInterruption,
        SocketError.HostUnreachable   => FailureCategory.NetworkInterruption,
        SocketError.AccessDenied      => FailureCategory.PermissionDenied,
        _                             => FailureCategory.NetworkInterruption,
    };

    private static FailureCategory ClassifyIo(IOException ex)
    {
        var hr = ex.HResult;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            switch (hr)
            {
                case HResultErrorDiskFull:
                case HResultErrorHandleDiskFull:
                    return FailureCategory.DiskFull;
                case HResultErrorAccessDenied:
                    return FailureCategory.PermissionDenied;
                case HResultErrorSharingViolation:
                case HResultErrorLockViolation:
                    return FailureCategory.IoFailure;
            }
        }
        else
        {
            var errno = hr & 0xFFFF;
            if (errno == PosixEnospc || errno == PosixEdquot) return FailureCategory.DiskFull;
            if (errno == PosixEacces || errno == PosixEperm)   return FailureCategory.PermissionDenied;
        }

        // Fallback: sniff message for portable disk-full signals (some runtimes drop HResult).
        var msg = ex.Message;
        if (!string.IsNullOrEmpty(msg))
        {
            if (msg.Contains("disk full", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("no space left", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("insufficient space", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("not enough space", StringComparison.OrdinalIgnoreCase))
            {
                return FailureCategory.DiskFull;
            }
        }

        return FailureCategory.IoFailure;
    }

    private static FailureCategory ClassifyWin32(Win32Exception ex) => ex.NativeErrorCode switch
    {
        112 or 39 => FailureCategory.DiskFull,    // ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL
        5         => FailureCategory.PermissionDenied,
        _         => FailureCategory.IoFailure,
    };
}
