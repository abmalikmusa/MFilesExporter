using System.Data;
using MFilesExporter.Application.Abstractions.WorkClaiming;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Domain.Jobs;
using MFilesExporter.Domain.WorkClaiming;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;

namespace MFilesExporter.Persistence.Tracking.Sql;

/// <summary>
/// SQL Server implementation of the work-claiming port. Every method is a
/// single call into one atomic stored procedure — no client-side
/// coordination is required.
/// </summary>
internal sealed class SqlWorkClaimStore : IWorkClaimStore
{
    private static readonly SqlMetaData[] EnqueueBatchMeta =
    [
        new SqlMetaData("IdempotencyKey",     SqlDbType.Char, 64),
        new SqlMetaData("DocumentFilePartId", SqlDbType.BigInt),
        new SqlMetaData("VersionPartId",      SqlDbType.BigInt),
        new SqlMetaData("DataFileVersionId",  SqlDbType.BigInt),
        new SqlMetaData("Priority",           SqlDbType.Int),
        new SqlMetaData("MaxAttempts",        SqlDbType.Int),
    ];

    private readonly SqlExecutor _executor;
    private readonly TrackingDatabaseOptions _options;

    public SqlWorkClaimStore(SqlExecutor executor, TrackingDatabaseOptions options)
    {
        _executor = executor;
        _options = options;
    }

    public Task<int> EnqueueAsync(
        long exportJobId,
        IReadOnlyCollection<WorkItemEnqueueRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0) return Task.FromResult(0);

        return _executor.ExecuteWithConnectionAsync(async (connection, ct) =>
        {
            await using var cmd = new SqlCommand("dbo.usp_EnqueueWorkItems", connection)
            {
                CommandType    = CommandType.StoredProcedure,
                CommandTimeout = _options.CommandTimeoutSeconds,
            };
            cmd.Parameters.Add(new SqlParameter("@ExportJobId", SqlDbType.BigInt) { Value = exportJobId });
            cmd.Parameters.Add(new SqlParameter("@Items", SqlDbType.Structured)
            {
                TypeName = "dbo.udt_ExportWorkItemBatch",
                Value = ToTvpRecords(requests),
            });
            var outParam = cmd.Parameters.Add(new SqlParameter("@Enqueued", SqlDbType.Int)
            {
                Direction = ParameterDirection.Output,
            });

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return (int)outParam.Value!;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ClaimedWorkItem>> ClaimAsync(
        long exportJobId,
        long workerId,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        return _executor.ExecuteWithConnectionAsync<IReadOnlyList<ClaimedWorkItem>>(async (connection, ct) =>
        {
            await using var cmd = new SqlCommand("dbo.usp_ClaimWorkItems", connection)
            {
                CommandType    = CommandType.StoredProcedure,
                CommandTimeout = _options.CommandTimeoutSeconds,
            };
            cmd.Parameters.Add(new SqlParameter("@ExportJobId", SqlDbType.BigInt) { Value = exportJobId });
            cmd.Parameters.Add(new SqlParameter("@WorkerId", SqlDbType.BigInt) { Value = workerId });
            cmd.Parameters.Add(new SqlParameter("@BatchSize", SqlDbType.Int) { Value = batchSize });
            cmd.Parameters.Add(new SqlParameter("@LeaseDurationSec", SqlDbType.Int)
            {
                Value = (int)Math.Max(1, leaseDuration.TotalSeconds),
            });

            var result = new List<ClaimedWorkItem>(batchSize);

            await using var reader = await cmd.ExecuteReaderAsync(
                CommandBehavior.SingleResult | CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var workItemId       = reader.GetInt64(0);
                var idempotencyHex   = reader.GetString(1);
                var docPart          = reader.GetInt64(2);
                var verPart          = reader.GetInt64(3);
                var dataFileVer      = reader.GetInt64(4);
                var attemptCount     = reader.GetInt32(5);
                var maxAttempts      = reader.GetInt32(6);
                var claimToken       = reader.GetGuid(7);
                var expiresAt        = reader.GetDateTime(8);

                result.Add(new ClaimedWorkItem
                {
                    WorkItemId             = new WorkItemId(workItemId),
                    JobId                  = new ExportJobId(exportJobId),
                    IdempotencyKey         = IdempotencyKey.Parse(idempotencyHex),
                    DocumentFileVersionKey = new DocumentFileVersionKey(docPart, verPart),
                    DataFileVersionKey     = new DataFileVersionKey(docPart, dataFileVer),
                    ClaimToken             = new ClaimToken(claimToken),
                    LeaseExpiresAtUtc      = new DateTimeOffset(
                        DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)),
                    AttemptNumber          = attemptCount,
                    MaxAttempts            = maxAttempts,
                });
            }
            return result;
        }, cancellationToken);
    }

    public Task<DateTimeOffset?> RenewAsync(
        WorkItemId workItemId,
        ClaimToken token,
        TimeSpan extension,
        CancellationToken cancellationToken)
    {
        return _executor.ExecuteWithConnectionAsync<DateTimeOffset?>(async (connection, ct) =>
        {
            await using var cmd = new SqlCommand("dbo.usp_RenewWorkItemLease", connection)
            {
                CommandType    = CommandType.StoredProcedure,
                CommandTimeout = _options.CommandTimeoutSeconds,
            };
            cmd.Parameters.Add(new SqlParameter("@WorkItemId", SqlDbType.BigInt) { Value = workItemId.Value });
            cmd.Parameters.Add(new SqlParameter("@ClaimToken", SqlDbType.UniqueIdentifier) { Value = token.Value });
            cmd.Parameters.Add(new SqlParameter("@ExtendBySec", SqlDbType.Int)
            {
                Value = (int)Math.Max(1, extension.TotalSeconds),
            });
            var extended = cmd.Parameters.Add(new SqlParameter("@Extended", SqlDbType.Bit)
            {
                Direction = ParameterDirection.Output,
            });
            var newExpires = cmd.Parameters.Add(new SqlParameter("@NewExpiresAtUtc", SqlDbType.DateTime2)
            {
                Precision = 3,
                Direction = ParameterDirection.Output,
            });

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            if (extended.Value is bool b && b && newExpires.Value is DateTime dt)
            {
                return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            }
            return null;
        }, cancellationToken);
    }

    public Task<bool> CompleteAsync(
        WorkItemId workItemId,
        ClaimToken token,
        string outputPath,
        string checksum,
        long bytesWritten,
        CancellationToken cancellationToken)
    {
        return _executor.ExecuteWithConnectionAsync(async (connection, ct) =>
        {
            await using var cmd = new SqlCommand("dbo.usp_CompleteWorkItem", connection)
            {
                CommandType    = CommandType.StoredProcedure,
                CommandTimeout = _options.CommandTimeoutSeconds,
            };
            cmd.Parameters.Add(new SqlParameter("@WorkItemId", SqlDbType.BigInt) { Value = workItemId.Value });
            cmd.Parameters.Add(new SqlParameter("@ClaimToken", SqlDbType.UniqueIdentifier) { Value = token.Value });
            cmd.Parameters.Add(new SqlParameter("@OutputPath", SqlDbType.NVarChar, 1024) { Value = outputPath });
            cmd.Parameters.Add(new SqlParameter("@Checksum", SqlDbType.Char, 64) { Value = checksum });
            cmd.Parameters.Add(new SqlParameter("@BytesWritten", SqlDbType.BigInt) { Value = bytesWritten });
            var completed = cmd.Parameters.Add(new SqlParameter("@Completed", SqlDbType.Bit)
            {
                Direction = ParameterDirection.Output,
            });

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return completed.Value is bool b && b;
        }, cancellationToken);
    }

    public Task<bool> FailAsync(
        WorkItemId workItemId,
        ClaimToken token,
        string reason,
        bool isPermanent,
        TimeSpan backoff,
        CancellationToken cancellationToken)
    {
        return _executor.ExecuteWithConnectionAsync(async (connection, ct) =>
        {
            await using var cmd = new SqlCommand("dbo.usp_FailWorkItem", connection)
            {
                CommandType    = CommandType.StoredProcedure,
                CommandTimeout = _options.CommandTimeoutSeconds,
            };
            cmd.Parameters.Add(new SqlParameter("@WorkItemId", SqlDbType.BigInt) { Value = workItemId.Value });
            cmd.Parameters.Add(new SqlParameter("@ClaimToken", SqlDbType.UniqueIdentifier) { Value = token.Value });
            cmd.Parameters.Add(new SqlParameter("@FailureReason", SqlDbType.NVarChar, 2000) { Value = reason });
            cmd.Parameters.Add(new SqlParameter("@IsPermanent", SqlDbType.Bit) { Value = isPermanent });
            cmd.Parameters.Add(new SqlParameter("@BackoffSeconds", SqlDbType.Int)
            {
                Value = (int)Math.Max(0, backoff.TotalSeconds),
            });
            var recorded = cmd.Parameters.Add(new SqlParameter("@Recorded", SqlDbType.Bit)
            {
                Direction = ParameterDirection.Output,
            });

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return recorded.Value is bool b && b;
        }, cancellationToken);
    }

    public Task<int> ReclaimExpiredAsync(
        TimeSpan retryBackoff,
        int maxRows,
        CancellationToken cancellationToken)
    {
        return _executor.ExecuteWithConnectionAsync(async (connection, ct) =>
        {
            await using var cmd = new SqlCommand("dbo.usp_ReclaimExpiredLeases", connection)
            {
                CommandType    = CommandType.StoredProcedure,
                CommandTimeout = _options.CommandTimeoutSeconds,
            };
            cmd.Parameters.Add(new SqlParameter("@BackoffSeconds", SqlDbType.Int)
            {
                Value = (int)Math.Max(0, retryBackoff.TotalSeconds),
            });
            cmd.Parameters.Add(new SqlParameter("@MaxRows", SqlDbType.Int) { Value = maxRows });
            var reclaimed = cmd.Parameters.Add(new SqlParameter("@Reclaimed", SqlDbType.Int)
            {
                Direction = ParameterDirection.Output,
            });

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return (int)reclaimed.Value!;
        }, cancellationToken);
    }

    private static IEnumerable<SqlDataRecord> ToTvpRecords(IReadOnlyCollection<WorkItemEnqueueRequest> reqs)
    {
        foreach (var r in reqs)
        {
            var rec = new SqlDataRecord(EnqueueBatchMeta);
            rec.SetString(0, r.IdempotencyKey.ToHex());
            rec.SetInt64(1, r.DocumentFileVersionKey.DocumentFilePartId);
            rec.SetInt64(2, r.DocumentFileVersionKey.VersionPartId);
            rec.SetInt64(3, r.DataFileVersionKey.DataFileVersionId);
            rec.SetInt32(4, r.Priority);
            rec.SetInt32(5, r.MaxAttempts);
            yield return rec;
        }
    }
}
