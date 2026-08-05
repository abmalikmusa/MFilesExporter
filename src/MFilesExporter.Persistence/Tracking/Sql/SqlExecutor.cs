using System.Data;
using MFilesExporter.Configuration.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Persistence.Tracking.Sql;

/// <summary>
/// Thin wrapper that:
///   * opens a connection via the tracking factory
///   * configures a stored-procedure SqlCommand with the configured timeout
///   * retries on transient failures with exponential backoff + jitter
///
/// Repositories build the command by handing back a small delegate that
/// receives the pre-configured SqlCommand and drives the ADO.NET calls
/// (SqlDataReader, ExecuteScalar, etc.). All BLOB reads are streamed —
/// no reader ever materializes rows into a DataTable, and TVPs are streamed
/// via IEnumerable&lt;SqlDataRecord&gt;.
/// </summary>
public sealed class SqlExecutor
{
    private readonly ITrackingSqlConnectionFactory _connectionFactory;
    private readonly TrackingDatabaseOptions _options;
    private readonly ILogger<SqlExecutor> _logger;

    public SqlExecutor(
        ITrackingSqlConnectionFactory connectionFactory,
        TrackingDatabaseOptions options,
        ILogger<SqlExecutor> logger)
    {
        _connectionFactory = connectionFactory;
        _options = options;
        _logger = logger;
    }

    public Task ExecuteNonQueryAsync(
        string storedProcedure,
        Action<SqlCommand> configureParameters,
        CancellationToken cancellationToken)
    {
        return ExecuteWithRetryAsync(
            storedProcedure,
            configureParameters,
            async (cmd, ct) =>
            {
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public Task<T> ExecuteScalarAsync<T>(
        string storedProcedure,
        Action<SqlCommand> configureParameters,
        CancellationToken cancellationToken)
    {
        return ExecuteWithRetryAsync(
            storedProcedure,
            configureParameters,
            async (cmd, ct) =>
            {
                var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (raw is null || raw is DBNull)
                {
                    return default!;
                }
                return (T)Convert.ChangeType(raw, typeof(T), System.Globalization.CultureInfo.InvariantCulture)!;
            },
            cancellationToken);
    }

    public Task<T> ExecuteReaderAsync<T>(
        string storedProcedure,
        Action<SqlCommand> configureParameters,
        Func<SqlDataReader, CancellationToken, Task<T>> readerHandler,
        CommandBehavior commandBehavior = CommandBehavior.Default,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithRetryAsync(
            storedProcedure,
            configureParameters,
            async (cmd, ct) =>
            {
                await using var reader = await cmd.ExecuteReaderAsync(commandBehavior, ct).ConfigureAwait(false);
                return await readerHandler(reader, ct).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// Executes a caller-supplied delegate against an open SqlConnection with
    /// retry semantics. Repositories that need to stream results (async
    /// enumerables) call this variant and manage the reader themselves.
    /// </summary>
    public async Task<T> ExecuteWithConnectionAsync<T>(
        Func<SqlConnection, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        var maxAttempts = 5;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            SqlConnection? connection = null;
            try
            {
                connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
                return await operation(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (SqlErrorClassifier.IsTransient(ex) && attempt < maxAttempts)
            {
                var delay = ComputeBackoff(attempt);
                _logger.LogWarning(ex, "SQL transient failure (attempt {Attempt}/{Max}); retrying in {Delay}",
                    attempt, maxAttempts, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        string storedProcedure,
        Action<SqlCommand> configureParameters,
        Func<SqlCommand, CancellationToken, Task<T>> execute,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        var maxAttempts = 5;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            SqlConnection? connection = null;
            SqlCommand? command = null;
            try
            {
                connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
                command = new SqlCommand(storedProcedure, connection)
                {
                    CommandType    = CommandType.StoredProcedure,
                    CommandTimeout = _options.CommandTimeoutSeconds,
                };
                configureParameters(command);

                return await execute(command, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (SqlErrorClassifier.IsTransient(ex) && attempt < maxAttempts)
            {
                var delay = ComputeBackoff(attempt);
                _logger.LogWarning(ex, "SQL transient failure calling {Sp} (attempt {Attempt}/{Max}); retrying in {Delay}",
                    storedProcedure, attempt, maxAttempts, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (command is not null) await command.DisposeAsync().ConfigureAwait(false);
                if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan ComputeBackoff(int attempt)
    {
        // Exponential (250, 500, 1000, 2000, 4000 ms) with ±25% jitter.
        var baseMs = 250d * Math.Pow(2, attempt - 1);
        var jitter = (System.Security.Cryptography.RandomNumberGenerator.GetInt32(-25, 26)) / 100d;
        return TimeSpan.FromMilliseconds(baseMs * (1 + jitter));
    }
}
