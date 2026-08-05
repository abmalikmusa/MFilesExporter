using System.Globalization;
using Dapper;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Shared.Collections;
using MFilesExporter.Shared.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Persistence.State;

/// <summary>
/// SQLite-backed state store. WAL journaling, memory-mapped I/O optional,
/// single long-lived writer connection guarded by an async mutex. Reads open
/// pool connections in read-only mode.
/// </summary>
internal sealed class SqliteStateStore : IExportStateStore, IAsyncDisposable
{
    private readonly StateStoreOptions _options;
    private readonly ILogger<SqliteStateStore> _logger;
    private readonly SemaphoreSlim _writerLock = new(1, 1);
    private SqliteConnection? _writerConnection;
    private bool _initialized;
    private bool _disposed;

    public SqliteStateStore(StateStoreOptions options, ILogger<SqliteStateStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        await _writerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            FileSystemHelpers.EnsureDirectoryFor(_options.ConnectionString);
            _writerConnection = new SqliteConnection(BuildConnectionString(readOnly: false));
            await _writerConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ApplyPragmasAsync(_writerConnection, cancellationToken).ConfigureAwait(false);
            await ApplySchemaAsync(_writerConnection, cancellationToken).ConfigureAwait(false);
            _initialized = true;

            _logger.LogInformation("State store initialized at {Path}", _options.ConnectionString);
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public async Task<DocumentFileVersionKey> GetCheckpointAsync(string partitionKey, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenReadAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<(long Part, long Ver)?>(
            new CommandDefinition(
                @"SELECT last_document_file_part_id, last_version_part_id
                  FROM checkpoints WHERE partition_key = @Partition;",
                new { Partition = partitionKey },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? DocumentFileVersionKey.Origin : new DocumentFileVersionKey(row.Value.Part, row.Value.Ver);
    }

    public async Task SaveCheckpointAsync(string partitionKey, DocumentFileVersionKey checkpoint, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writerConnection!.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO checkpoints (partition_key, last_document_file_part_id, last_version_part_id, updated_at_utc)
                  VALUES (@Partition, @Part, @Ver, @Now)
                  ON CONFLICT(partition_key) DO UPDATE SET
                    last_document_file_part_id = MAX(excluded.last_document_file_part_id, checkpoints.last_document_file_part_id),
                    last_version_part_id = CASE
                      WHEN excluded.last_document_file_part_id > checkpoints.last_document_file_part_id
                        THEN excluded.last_version_part_id
                      WHEN excluded.last_document_file_part_id = checkpoints.last_document_file_part_id
                        THEN MAX(excluded.last_version_part_id, checkpoints.last_version_part_id)
                      ELSE checkpoints.last_version_part_id
                    END,
                    updated_at_utc = excluded.updated_at_utc;",
                new
                {
                    Partition = partitionKey,
                    Part = checkpoint.DocumentFilePartId,
                    Ver = checkpoint.VersionPartId,
                    Now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public Task RecordOutcomeAsync(ExportOutcome outcome, CancellationToken cancellationToken) =>
        RecordOutcomesAsync(new[] { outcome }, cancellationToken);

    public async Task RecordOutcomesAsync(IReadOnlyCollection<ExportOutcome> outcomes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count == 0) return;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _writerConnection!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            const string sql = @"INSERT INTO export_outcomes
                (idempotency_key, document_file_part_id, version_part_id, data_file_version_id,
                 status, bytes_written, output_path, checksum, failure_reason, observed_at_utc, attempt_number)
              VALUES
                (@Key, @DocPart, @VerPart, @DataFileVer, @Status, @Bytes, @Path, @Checksum, @Reason, @Observed, @Attempt)
              ON CONFLICT(idempotency_key) DO UPDATE SET
                status = excluded.status,
                bytes_written = excluded.bytes_written,
                output_path = excluded.output_path,
                checksum = excluded.checksum,
                failure_reason = excluded.failure_reason,
                observed_at_utc = excluded.observed_at_utc,
                attempt_number = excluded.attempt_number;";

            foreach (var o in outcomes)
            {
                await _writerConnection.ExecuteAsync(new CommandDefinition(sql, new
                {
                    Key = o.IdempotencyKey.ToArray(),
                    DocPart = o.DocumentFileVersionKey.DocumentFilePartId,
                    VerPart = o.DocumentFileVersionKey.VersionPartId,
                    DataFileVer = o.DataFileVersionKey.DataFileVersionId,
                    Status = (int)o.Status,
                    Bytes = o.BytesWritten,
                    Path = (object?)o.OutputPath ?? DBNull.Value,
                    Checksum = (object?)o.Checksum ?? DBNull.Value,
                    Reason = (object?)o.FailureReason ?? DBNull.Value,
                    Observed = o.ObservedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    Attempt = o.AttemptNumber,
                }, transaction: tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public async Task<ExportStatus> GetStatusAsync(IdempotencyKey key, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenReadAsync(cancellationToken).ConfigureAwait(false);

        var status = await connection.QuerySingleOrDefaultAsync<int?>(
            new CommandDefinition(
                "SELECT status FROM export_outcomes WHERE idempotency_key = @Key;",
                new { Key = key.ToArray() },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return status.HasValue ? (ExportStatus)status.Value : ExportStatus.Unknown;
    }

    public async Task<IReadOnlyDictionary<IdempotencyKey, ExportStatus>> GetStatusesAsync(
        IReadOnlyCollection<IdempotencyKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var result = new Dictionary<IdempotencyKey, ExportStatus>(keys.Count);
        if (keys.Count == 0) return result;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenReadAsync(cancellationToken).ConfigureAwait(false);

        foreach (var chunk in keys.ChunkBy(500))
        {
            var parameters = new DynamicParameters();
            var placeholders = new List<string>(chunk.Count);
            for (var i = 0; i < chunk.Count; i++)
            {
                var name = "@K" + i.ToString(CultureInfo.InvariantCulture);
                parameters.Add(name, chunk[i].ToArray());
                placeholders.Add(name);
            }
            var sql = $"SELECT idempotency_key, status FROM export_outcomes WHERE idempotency_key IN ({string.Join(",", placeholders)});";
            var rows = await connection.QueryAsync<(byte[] Key, int Status)>(
                new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);

            foreach (var r in rows)
            {
                result[IdempotencyKey.Parse(Convert.ToHexString(r.Key).ToLowerInvariant())] = (ExportStatus)r.Status;
            }
        }
        return result;
    }

    public async Task<StateStoreCounters> GetCountersAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenReadAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<(long Recorded, long Succeeded, long Failed, long Skipped, long Bytes)?>(
            new CommandDefinition(
                @"SELECT total_recorded, total_succeeded, total_failed, total_skipped, total_bytes_written
                  FROM export_counters WHERE singleton = 0;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null
            ? new StateStoreCounters(0, 0, 0, 0, 0)
            : new StateStoreCounters(row.Value.Recorded, row.Value.Succeeded, row.Value.Failed, row.Value.Skipped, row.Value.Bytes);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_writerConnection is not null)
        {
            try
            {
                await using var cmd = _writerConnection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Final WAL checkpoint failed");
            }
            await _writerConnection.DisposeAsync().ConfigureAwait(false);
            _writerConnection = null;
        }
        _writerLock.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized) await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenReadAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(BuildConnectionString(readOnly: false));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private string BuildConnectionString(bool readOnly) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = _options.ConnectionString,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        }.ToString();

    private async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var pragmas = string.Join(" ", new[]
        {
            "PRAGMA journal_mode=WAL;",
            "PRAGMA synchronous=NORMAL;",
            "PRAGMA busy_timeout=15000;",
            $"PRAGMA cache_size=-{_options.CacheSizeKib};",
            $"PRAGMA mmap_size={(_options.EnableMemoryMappedIo ? 268435456 : 0)};",
            "PRAGMA temp_store=MEMORY;",
            "PRAGMA foreign_keys=ON;",
        });
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplySchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var statement in StateSchema.Statements)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
