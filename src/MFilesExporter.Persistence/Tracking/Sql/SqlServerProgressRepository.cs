using System.Data;
using Microsoft.Data.SqlClient.Server;
using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Models.Tracking;
using MFilesExporter.Configuration.Options;
using Microsoft.Data.SqlClient;

namespace MFilesExporter.Persistence.Tracking.Sql;

internal sealed class SqlServerProgressRepository : IExportProgressRepository
{
    private static readonly SqlMetaData[] ProgressBatchMeta =
    [
        new SqlMetaData("ExportJobId",             SqlDbType.BigInt),
        new SqlMetaData("ExportWorkerId",          SqlDbType.BigInt),
        new SqlMetaData("SnapshotAtUtc",           SqlDbType.DateTime2, precision: 3, scale: 3),
        new SqlMetaData("TotalRecorded",           SqlDbType.BigInt),
        new SqlMetaData("TotalSucceeded",          SqlDbType.BigInt),
        new SqlMetaData("TotalFailed",             SqlDbType.BigInt),
        new SqlMetaData("TotalSkipped",            SqlDbType.BigInt),
        new SqlMetaData("TotalBytesWritten",       SqlDbType.BigInt),
        new SqlMetaData("DocumentsPerSecond",      SqlDbType.Decimal, precision: 18, scale: 4),
        new SqlMetaData("MebibytesPerSecond",      SqlDbType.Decimal, precision: 18, scale: 4),
        new SqlMetaData("LastDocumentFilePartId",  SqlDbType.BigInt),
        new SqlMetaData("LastVersionPartId",       SqlDbType.BigInt),
    ];

    private readonly SqlExecutor _executor;
    private readonly TrackingDatabaseOptions _options;

    public SqlServerProgressRepository(SqlExecutor executor, TrackingDatabaseOptions options)
    {
        _executor = executor;
        _options = options;
    }

    public Task RecordAsync(ExportProgressRecord snapshot, CancellationToken cancellationToken)
    {
        return _executor.ExecuteNonQueryAsync(
            "dbo.usp_RecordExportProgress",
            cmd =>
            {
                cmd.Parameters.Add(new SqlParameter("@ExportJobId", SqlDbType.BigInt) { Value = snapshot.ExportJobId });
                cmd.Parameters.Add(new SqlParameter("@ExportWorkerId", SqlDbType.BigInt)
                {
                    Value = (object?)snapshot.ExportWorkerId ?? DBNull.Value,
                });
                cmd.Parameters.Add(new SqlParameter("@TotalRecorded", SqlDbType.BigInt) { Value = snapshot.TotalRecorded });
                cmd.Parameters.Add(new SqlParameter("@TotalSucceeded", SqlDbType.BigInt) { Value = snapshot.TotalSucceeded });
                cmd.Parameters.Add(new SqlParameter("@TotalFailed", SqlDbType.BigInt) { Value = snapshot.TotalFailed });
                cmd.Parameters.Add(new SqlParameter("@TotalSkipped", SqlDbType.BigInt) { Value = snapshot.TotalSkipped });
                cmd.Parameters.Add(new SqlParameter("@TotalBytesWritten", SqlDbType.BigInt) { Value = snapshot.TotalBytesWritten });
                cmd.Parameters.Add(new SqlParameter("@DocumentsPerSecond", SqlDbType.Decimal)
                {
                    Precision = 18, Scale = 4,
                    Value = (object?)snapshot.DocumentsPerSecond ?? DBNull.Value,
                });
                cmd.Parameters.Add(new SqlParameter("@MebibytesPerSecond", SqlDbType.Decimal)
                {
                    Precision = 18, Scale = 4,
                    Value = (object?)snapshot.MebibytesPerSecond ?? DBNull.Value,
                });
                cmd.Parameters.Add(new SqlParameter("@LastDocumentFilePartId", SqlDbType.BigInt)
                {
                    Value = (object?)snapshot.LastDocumentFilePartId ?? DBNull.Value,
                });
                cmd.Parameters.Add(new SqlParameter("@LastVersionPartId", SqlDbType.BigInt)
                {
                    Value = (object?)snapshot.LastVersionPartId ?? DBNull.Value,
                });
                cmd.Parameters.Add(new SqlParameter("@ActorName", SqlDbType.NVarChar, 200) { Value = ActorContext.Resolve(_options) });
                cmd.Parameters.Add(new SqlParameter("@ExportProgressId", SqlDbType.BigInt)
                {
                    Direction = ParameterDirection.Output,
                });
            },
            cancellationToken);
    }

    public Task RecordBatchAsync(IReadOnlyCollection<ExportProgressRecord> snapshots, CancellationToken cancellationToken)
    {
        if (snapshots.Count == 0) return Task.CompletedTask;

        return _executor.ExecuteNonQueryAsync(
            "dbo.usp_RecordExportProgressBatch",
            cmd =>
            {
                var p = cmd.Parameters.Add(new SqlParameter("@Progress", SqlDbType.Structured)
                {
                    TypeName = "dbo.udt_ExportProgressBatch",
                    Value    = ToTvpRecords(snapshots),
                });
                cmd.Parameters.Add(new SqlParameter("@ActorName", SqlDbType.NVarChar, 200) { Value = ActorContext.Resolve(_options) });
            },
            cancellationToken);
    }

    public Task<ExportProgressRecord?> GetLatestAsync(long exportJobId, CancellationToken cancellationToken)
    {
        return _executor.ExecuteWithConnectionAsync<ExportProgressRecord?>(async (connection, ct) =>
        {
            await using var cmd = new SqlCommand("dbo.usp_GetLatestProgress", connection)
            {
                CommandType    = CommandType.StoredProcedure,
                CommandTimeout = _options.CommandTimeoutSeconds,
            };
            cmd.Parameters.Add(new SqlParameter("@ExportJobId", SqlDbType.BigInt) { Value = exportJobId });

            await using var reader = await cmd.ExecuteReaderAsync(
                CommandBehavior.SingleResult | CommandBehavior.SingleRow,
                ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new ExportProgressRecord
            {
                ExportProgressId       = reader.GetInt64(0),
                ExportJobId            = reader.GetInt64(1),
                ExportWorkerId         = await reader.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : reader.GetInt64(2),
                SnapshotAtUtc          = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                TotalRecorded          = reader.GetInt64(4),
                TotalSucceeded         = reader.GetInt64(5),
                TotalFailed            = reader.GetInt64(6),
                TotalSkipped           = reader.GetInt64(7),
                TotalBytesWritten      = reader.GetInt64(8),
                DocumentsPerSecond     = await reader.IsDBNullAsync(9, ct).ConfigureAwait(false) ? null : reader.GetDecimal(9),
                MebibytesPerSecond     = await reader.IsDBNullAsync(10, ct).ConfigureAwait(false) ? null : reader.GetDecimal(10),
                LastDocumentFilePartId = await reader.IsDBNullAsync(11, ct).ConfigureAwait(false) ? null : reader.GetInt64(11),
                LastVersionPartId      = await reader.IsDBNullAsync(12, ct).ConfigureAwait(false) ? null : reader.GetInt64(12),
            };
        }, cancellationToken);
    }

    /* -----------------------------------------------------------------
     * TVP marshalling: yields IEnumerable<SqlDataRecord> directly so we
     * never materialize the batch into a DataTable.
     * ----------------------------------------------------------------- */
    private static IEnumerable<SqlDataRecord> ToTvpRecords(IReadOnlyCollection<ExportProgressRecord> snapshots)
    {
        foreach (var s in snapshots)
        {
            var record = new SqlDataRecord(ProgressBatchMeta);
            record.SetInt64(0, s.ExportJobId);
            if (s.ExportWorkerId is long wid) record.SetInt64(1, wid); else record.SetDBNull(1);
            record.SetDateTime(2, s.SnapshotAtUtc == default ? DateTime.UtcNow : s.SnapshotAtUtc);
            record.SetInt64(3, s.TotalRecorded);
            record.SetInt64(4, s.TotalSucceeded);
            record.SetInt64(5, s.TotalFailed);
            record.SetInt64(6, s.TotalSkipped);
            record.SetInt64(7, s.TotalBytesWritten);
            if (s.DocumentsPerSecond is decimal dps) record.SetDecimal(8, dps); else record.SetDBNull(8);
            if (s.MebibytesPerSecond is decimal mps) record.SetDecimal(9, mps); else record.SetDBNull(9);
            if (s.LastDocumentFilePartId is long lp) record.SetInt64(10, lp); else record.SetDBNull(10);
            if (s.LastVersionPartId is long lv) record.SetInt64(11, lv); else record.SetDBNull(11);
            yield return record;
        }
    }
}
