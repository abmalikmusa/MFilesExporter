using System.Data;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Domain.Exceptions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Persistence.MFiles.Streaming;

/// <summary>
/// Default <see cref="ISqlStreamingEngine"/> implementation. Backed by
/// Microsoft.Data.SqlClient, uses <see cref="SqlDataReader"/> exclusively
/// (no <c>DataTable</c>, no <c>DataSet</c>, no EF), opens BLOB streams via
/// <c>SqlDataReader.GetBytes(...)</c> (wrapped by
/// <see cref="SqlBytesReadStream"/>) under
/// <see cref="CommandBehavior.SequentialAccess"/>.
///
/// Every SQL operation is executed through an internal retry loop with
/// exponential backoff and jitter. Transient failures are classified by
/// <see cref="SqlTransientErrorClassifier"/>; deterministic failures
/// propagate.
/// </summary>
public sealed class SqlStreamingEngine : ISqlStreamingEngine
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly MFilesSourceOptions _sourceOptions;
    private readonly SqlStreamingOptions _streamingOptions;
    private readonly ILogger<SqlStreamingEngine> _logger;

    public SqlStreamingEngine(
        ISqlConnectionFactory connectionFactory,
        MFilesSourceOptions sourceOptions,
        SqlStreamingOptions streamingOptions,
        ILogger<SqlStreamingEngine> logger)
    {
        _connectionFactory = connectionFactory;
        _sourceOptions = sourceOptions;
        _streamingOptions = streamingOptions;
        _logger = logger;
    }

    public async IAsyncEnumerable<StreamedDocumentDescriptor> StreamAsync(
        DocumentFileVersionKey exclusiveLowerBound,
        SqlStreamingRunOptions? runOptions,
        IProgress<SqlStreamingProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        runOptions ??= new SqlStreamingRunOptions();

        var fetchSize = runOptions.FetchSize ?? _streamingOptions.FetchSize;
        var metaTimeout = runOptions.CommandTimeout
            ?? TimeSpan.FromSeconds(_streamingOptions.CommandTimeoutSeconds);
        var blobTimeout = runOptions.BlobCommandTimeout
            ?? TimeSpan.FromSeconds(_streamingOptions.BlobCommandTimeoutSeconds);
        var maxRetries = runOptions.MaxRetryAttempts ?? _streamingOptions.MaxRetryAttempts;

        var cursor = exclusiveLowerBound;
        var sql = MFilesQueries.EnumerationQuery(_sourceOptions.Tables, _streamingOptions.UseReadUncommittedForEnumeration);

        long rowsYielded = 0;
        long pagesFetched = 0;
        long retryAttempts = 0;
        var startedAt = DateTimeOffset.UtcNow;
        var lastProgress = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await FetchPageWithRetryAsync(
                sql, cursor, fetchSize, metaTimeout,
                maxRetries,
                onAttempt: attempt => Interlocked.Increment(ref retryAttempts),
                cancellationToken).ConfigureAwait(false);

            pagesFetched++;

            if (page.Count == 0)
            {
                EmitProgress(progress, rowsYielded, pagesFetched, retryAttempts, cursor, startedAt, force: true);
                yield break;
            }

            foreach (var descriptor in page)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Capture per-descriptor state so the OpenContentStream
                // closure never accidentally sees a mutated outer cursor.
                var localDescriptor = descriptor;
                yield return new StreamedDocumentDescriptor(
                    localDescriptor,
                    openContent: ct => OpenContentWithRetryAsync(
                        localDescriptor.DataFileVersionKey, blobTimeout, maxRetries, ct));

                rowsYielded++;

                if (lastProgress.Elapsed >= _streamingOptions.ProgressReportInterval)
                {
                    EmitProgress(progress, rowsYielded, pagesFetched, retryAttempts,
                        localDescriptor.DocumentFileVersionKey, startedAt, force: false);
                    lastProgress.Restart();
                }
            }

            cursor = page[^1].DocumentFileVersionKey;

            // Short page = source has no more rows > cursor.
            if (page.Count < fetchSize)
            {
                EmitProgress(progress, rowsYielded, pagesFetched, retryAttempts, cursor, startedAt, force: true);
                yield break;
            }
        }
    }

    /* -------------------------------------------------------------------------
     * Metadata page fetch — one SqlCommand per keyset page.
     * ------------------------------------------------------------------------- */
    private async Task<IReadOnlyList<DocumentDescriptor>> FetchPageWithRetryAsync(
        string sql,
        DocumentFileVersionKey cursor,
        int fetchSize,
        TimeSpan commandTimeout,
        int maxRetries,
        Action<int> onAttempt,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                return await FetchPageOnceAsync(sql, cursor, fetchSize, commandTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (SqlTransientErrorClassifier.IsTransient(ex) && attempt < maxRetries)
            {
                onAttempt(attempt);
                var delay = ComputeBackoff(attempt);
                _logger.LogWarning(ex,
                    "SQL streaming: transient failure on metadata fetch (attempt {Attempt}/{Max}); retrying in {Delay}",
                    attempt, maxRetries, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<DocumentDescriptor>> FetchPageOnceAsync(
        string sql,
        DocumentFileVersionKey cursor,
        int fetchSize,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        var results = new List<DocumentDescriptor>(fetchSize);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandType    = CommandType.Text,
            CommandTimeout = (int)commandTimeout.TotalSeconds,
        };
        command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = fetchSize;
        command.Parameters.Add("@LastDocumentFilePartId", SqlDbType.BigInt).Value = cursor.DocumentFilePartId;
        command.Parameters.Add("@LastVersionPartId", SqlDbType.BigInt).Value = cursor.VersionPartId;

        // SequentialAccess is applied even on the metadata reader so no row
        // is buffered before it is consumed. GetBytes() is only used for
        // the varbinary column in the content reader — the metadata columns
        // are all fixed-width or short strings.
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleResult | CommandBehavior.SequentialAccess,
            cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var docPart      = reader.GetInt64(0);
            // ID_VERSIONPART is INT in the M-Files vault schema, not BIGINT.
            // Read as Int32 and widen so the domain's long-typed key holds it.
            var verPart      = (long)reader.GetInt32(1);
            var title        = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                ? string.Empty : reader.GetString(2);
            var ext          = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
                ? string.Empty : reader.GetString(3);
            var dataFileVer  = reader.GetInt64(4);
            var logicalSize  = reader.GetInt64(5);
            var physicalSize = reader.GetInt64(6);
            var lastWrite    = await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                ? DateTime.UnixEpoch : reader.GetDateTime(7);

            results.Add(new DocumentDescriptor(
                new DocumentFileVersionKey(docPart, verPart),
                new DataFileVersionKey(docPart, dataFileVer),
                title, ext, logicalSize, physicalSize,
                DateTime.SpecifyKind(lastWrite, DateTimeKind.Utc)));
        }
        return results;
    }

    /* -------------------------------------------------------------------------
     * Content (BLOB) stream open — one SqlCommand per document. The returned
     * stream owns the connection + reader lifetime; disposing it releases
     * both to the pool.
     * ------------------------------------------------------------------------- */
    private async Task<DocumentContentStream> OpenContentWithRetryAsync(
        DataFileVersionKey key,
        TimeSpan commandTimeout,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                return await OpenContentOnceAsync(key, commandTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (DocumentContentMissingException)
            {
                // Deterministic — do not retry.
                throw;
            }
            catch (Exception ex) when (SqlTransientErrorClassifier.IsTransient(ex) && attempt < maxRetries)
            {
                var delay = ComputeBackoff(attempt);
                _logger.LogWarning(ex,
                    "SQL streaming: transient failure opening BLOB {Key} (attempt {Attempt}/{Max}); retrying in {Delay}",
                    key, attempt, maxRetries, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<DocumentContentStream> OpenContentOnceAsync(
        DataFileVersionKey key,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        SqlConnection? connection = null;
        SqlCommand? command = null;
        SqlDataReader? reader = null;

        try
        {
            connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

            command = new SqlCommand(MFilesQueries.ContentQuery(_sourceOptions.Tables), connection)
            {
                CommandType    = CommandType.Text,
                CommandTimeout = (int)commandTimeout.TotalSeconds,
            };
            command.Parameters.Add("@DocumentFilePartId", SqlDbType.BigInt).Value = key.DocumentFilePartId;
            command.Parameters.Add("@DataFileVersionId", SqlDbType.BigInt).Value  = key.DataFileVersionId;

            // SequentialAccess + SingleRow: reader never buffers the BLOB row.
            reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleResult
              | CommandBehavior.SingleRow
              | CommandBehavior.SequentialAccess,
                cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new DocumentContentMissingException(key);
            }
            if (await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new DocumentContentMissingException(key);
            }

            // GetBytes()-based chunked stream — never allocates the whole BLOB.
            Stream stream = new SqlBytesReadStream(reader, ordinal: 0);

            // Transfer ownership.
            var readerRef = reader;
            var commandRef = command;
            var connectionRef = connection;
            reader = null;
            command = null;
            connection = null;

            return new DocumentContentStream(
                stream,
                length: -1,
                dispose: async () =>
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    await readerRef.DisposeAsync().ConfigureAwait(false);
                    await commandRef.DisposeAsync().ConfigureAwait(false);
                    await connectionRef.DisposeAsync().ConfigureAwait(false);
                });
        }
        catch
        {
            if (reader is not null) await reader.DisposeAsync().ConfigureAwait(false);
            if (command is not null) await command.DisposeAsync().ConfigureAwait(false);
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void EmitProgress(
        IProgress<SqlStreamingProgress>? progress,
        long rowsYielded,
        long pagesFetched,
        long retryAttempts,
        DocumentFileVersionKey cursor,
        DateTimeOffset startedAt,
        bool force)
    {
        if (progress is null) return;

        try
        {
            progress.Report(new SqlStreamingProgress
            {
                RowsYielded    = rowsYielded,
                PagesFetched   = pagesFetched,
                RetryAttempts  = retryAttempts,
                LastCursor     = cursor,
                ObservedAtUtc  = DateTimeOffset.UtcNow,
                Elapsed        = DateTimeOffset.UtcNow - startedAt,
            });
        }
        catch (Exception ex)
        {
            // A progress consumer must not fault the stream.
            _logger.LogWarning(ex, "SQL streaming: progress consumer threw; continuing.");
        }
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var baseMs = _streamingOptions.RetryBaseDelay.TotalMilliseconds
                   * Math.Pow(2, attempt - 1);
        var clamped = Math.Min(baseMs, _streamingOptions.RetryMaxDelay.TotalMilliseconds);
        var jitter = 0.75 + (System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 501) / 1000d);
        return TimeSpan.FromMilliseconds(clamped * jitter);
    }
}

/// <summary>
/// Distinguishes transient SQL failures (retry-worthy) from deterministic
/// failures (fail fast). Locality: kept inside the streaming module so it
/// can evolve independently of the broader Polly-based classifier used
/// elsewhere.
/// </summary>
internal static class SqlTransientErrorClassifier
{
    public static bool IsTransient(Exception ex) => ex switch
    {
        OperationCanceledException => false,
        SqlException sqlEx         => IsTransientSqlError(sqlEx.Number),
        System.IO.IOException      => true,
        TimeoutException           => true,
        _                          => false,
    };

    private static bool IsTransientSqlError(int number) => number switch
    {
        // Deadlock, lock timeout.
        1205 or 1222                                        => true,
        // Connection-level transients.
        -2 or 233 or 10053 or 10054 or 10060 or 121         => true,
        // Server-busy / Azure SQL throttling.
        40197 or 40501 or 40613 or 49918 or 49919 or 49920  => true,
        _                                                   => false,
    };
}
