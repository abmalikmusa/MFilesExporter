using System.Diagnostics;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Export.Checkpointing.WriteAheadLog;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Checkpointing;

/// <summary>
/// Default <see cref="ICheckpointEngine"/> — write-ahead-log first, SQL
/// Server tracking DB second, reconcile on recovery.
///
/// Save protocol:
/// <list type="number">
///   <item><description>Write WAL (durable in milliseconds, survives power/OS/app crash).</description></item>
///   <item><description>Write SQL Server (durable across nodes, retry handled by <c>SqlServerCheckpointRepository</c>).</description></item>
///   <item><description>If SQL fails but WAL succeeded, report partial success — the batch is still safe because the next save will retry SQL and recovery uses <c>max(WAL, SQL)</c>.</description></item>
/// </list>
///
/// Recovery protocol:
/// <list type="number">
///   <item><description>Read WAL (may be null).</description></item>
///   <item><description>Read SQL Server (may be null).</description></item>
///   <item><description>Return the higher of the two by cursor value.</description></item>
///   <item><description>If WAL &gt; SQL and reconciliation is enabled, back-fill SQL.</description></item>
/// </list>
/// </summary>
public sealed class CheckpointEngine : ICheckpointEngine
{
    private readonly ICheckpointWal _wal;
    private readonly IExportCheckpointRepository _sqlRepo;
    private readonly IClock _clock;
    private readonly CheckpointOptions _options;
    private readonly ILogger<CheckpointEngine> _logger;

    public CheckpointEngine(
        ICheckpointWal wal,
        IExportCheckpointRepository sqlRepo,
        IClock clock,
        CheckpointOptions options,
        ILogger<CheckpointEngine> logger)
    {
        _wal = wal;
        _sqlRepo = sqlRepo;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public async Task<CheckpointState> RecoverAsync(
        long jobId,
        string partitionKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);

        var walEntry = await SafeReadWalAsync(jobId, partitionKey, cancellationToken).ConfigureAwait(false);
        var sqlEntry = _options.PersistToTrackingDb
            ? await SafeReadSqlAsync(jobId, partitionKey, cancellationToken).ConfigureAwait(false)
            : null;

        if (walEntry is null && sqlEntry is null)
        {
            _logger.LogInformation(
                "No checkpoint found for job={JobId} partition={Partition} — starting at origin.",
                jobId, partitionKey);
            return CheckpointState.AtOrigin(_clock.UtcNow);
        }

        // Pick the higher cursor. When WAL == SQL, prefer the aggregate source.
        CheckpointSource source;
        DocumentFileVersionKey cursor;
        long docs;
        DateTimeOffset persistedAt;

        if (walEntry is not null && sqlEntry is null)
        {
            cursor = walEntry.Cursor;
            docs = walEntry.DocumentsProcessedInPartition;
            persistedAt = walEntry.PersistedAtUtc;
            source = CheckpointSource.Wal;
        }
        else if (walEntry is null && sqlEntry is not null)
        {
            cursor = sqlEntry.Cursor;
            docs = sqlEntry.DocumentsProcessed;
            persistedAt = sqlEntry.PersistedAtUtc;
            source = CheckpointSource.SqlServer;
        }
        else
        {
            // Both present. Compare.
            var cmp = walEntry!.Cursor.CompareTo(sqlEntry!.Cursor);
            if (cmp > 0)
            {
                cursor = walEntry.Cursor;
                docs = walEntry.DocumentsProcessedInPartition;
                persistedAt = walEntry.PersistedAtUtc;
                source = CheckpointSource.Wal;

                if (_options.ReconcileSqlOnRecovery)
                {
                    await ReconcileSqlToWalAsync(jobId, partitionKey, walEntry, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else if (cmp < 0)
            {
                cursor = sqlEntry.Cursor;
                docs = sqlEntry.DocumentsProcessed;
                persistedAt = sqlEntry.PersistedAtUtc;
                source = CheckpointSource.SqlServer;

                // WAL was behind — bring it up so subsequent saves are monotonic.
                await SafeAppendWalAsync(jobId, partitionKey,
                    new WalEntry(sqlEntry.Cursor, sqlEntry.DocumentsProcessed, sqlEntry.PersistedAtUtc),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Perfectly consistent.
                cursor = walEntry.Cursor;
                docs = Math.Max(walEntry.DocumentsProcessedInPartition, sqlEntry.DocumentsProcessed);
                persistedAt = walEntry.PersistedAtUtc > sqlEntry.PersistedAtUtc
                    ? walEntry.PersistedAtUtc
                    : sqlEntry.PersistedAtUtc;
                source = CheckpointSource.WalAndSql;
            }
        }

        _logger.LogInformation(
            "Checkpoint recovered for job={JobId} partition={Partition} — cursor={Cursor} docs={Docs} source={Source} persistedAt={Persisted:O}",
            jobId, partitionKey, cursor, docs, source, persistedAt);

        return new CheckpointState(cursor, docs, persistedAt, source);
    }

    public async Task<CheckpointSaveResult> SaveAsync(
        long jobId,
        string partitionKey,
        CheckpointCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentNullException.ThrowIfNull(candidate);

        var sw = Stopwatch.StartNew();
        var entry = new WalEntry(candidate.Cursor, candidate.DocumentsProcessedInPartition, _clock.UtcNow);

        // --- 1. WAL first — millisecond durability. ---
        bool walOk;
        try
        {
            await _wal.AppendAsync(jobId, partitionKey, entry, cancellationToken).ConfigureAwait(false);
            walOk = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "WAL append failed for job={JobId} partition={Partition}. Checkpoint durability is temporarily degraded.",
                jobId, partitionKey);
            walOk = false;
        }

        // --- 2. SQL Server tracking DB (optional, best-effort with resilience via repo). ---
        bool sqlOk = false;
        bool sqlAdvanced = false;
        string? warning = null;

        if (_options.PersistToTrackingDb)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_options.SqlSaveTimeout);

                sqlAdvanced = await _sqlRepo.SaveAsync(
                    jobId, partitionKey,
                    candidate.Cursor.DocumentFilePartId,
                    candidate.Cursor.VersionPartId,
                    candidate.DocumentsProcessedInPartition,
                    timeoutCts.Token).ConfigureAwait(false);
                sqlOk = true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                warning = "SQL checkpoint save timed out — WAL is authoritative.";
                _logger.LogWarning(warning);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warning = $"SQL checkpoint save failed: {ex.Message} — WAL is authoritative.";
                _logger.LogWarning(ex, "SQL checkpoint save failed for job={JobId} partition={Partition}", jobId, partitionKey);
            }
        }

        sw.Stop();
        return new CheckpointSaveResult
        {
            Advanced   = walOk || sqlAdvanced,
            WalWritten = walOk,
            SqlWritten = sqlOk,
            Elapsed    = sw.Elapsed,
            Warning    = warning,
        };
    }

    private async Task<WalEntry?> SafeReadWalAsync(long jobId, string partitionKey, CancellationToken ct)
    {
        try
        {
            return await _wal.ReadLatestAsync(jobId, partitionKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WAL read failed; treating as no-checkpoint.");
            return null;
        }
    }

    private async Task<SqlCheckpointSnapshot?> SafeReadSqlAsync(long jobId, string partitionKey, CancellationToken ct)
    {
        try
        {
            var record = await _sqlRepo.GetActiveAsync(jobId, partitionKey, ct).ConfigureAwait(false);
            if (record is null) return null;
            return new SqlCheckpointSnapshot(
                new DocumentFileVersionKey(record.LastDocumentFilePartId, record.LastVersionPartId),
                record.DocumentsProcessedInPartition ?? 0,
                new DateTimeOffset(DateTime.SpecifyKind(record.CheckpointAtUtc, DateTimeKind.Utc)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SQL checkpoint read failed; treating as no-checkpoint.");
            return null;
        }
    }

    private async Task ReconcileSqlToWalAsync(long jobId, string partitionKey, WalEntry wal, CancellationToken ct)
    {
        try
        {
            await _sqlRepo.SaveAsync(
                jobId, partitionKey,
                wal.Cursor.DocumentFilePartId,
                wal.Cursor.VersionPartId,
                wal.DocumentsProcessedInPartition,
                ct).ConfigureAwait(false);

            _logger.LogInformation(
                "SQL checkpoint caught up to WAL for job={JobId} partition={Partition} → {Cursor}",
                jobId, partitionKey, wal.Cursor);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "SQL reconciliation with WAL failed; will retry on next SaveAsync.");
        }
    }

    private async Task SafeAppendWalAsync(long jobId, string partitionKey, WalEntry entry, CancellationToken ct)
    {
        try
        {
            await _wal.AppendAsync(jobId, partitionKey, entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Backfill WAL append failed; not critical.");
        }
    }

    private sealed record SqlCheckpointSnapshot(
        DocumentFileVersionKey Cursor,
        long DocumentsProcessed,
        DateTimeOffset PersistedAtUtc);
}
