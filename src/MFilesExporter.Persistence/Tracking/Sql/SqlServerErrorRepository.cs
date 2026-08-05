using System.Data;
using Microsoft.Data.SqlClient.Server;
using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Models.Tracking;
using MFilesExporter.Configuration.Options;
using Microsoft.Data.SqlClient;

namespace MFilesExporter.Persistence.Tracking.Sql;

internal sealed class SqlServerErrorRepository : IExportErrorRepository
{
    private static readonly SqlMetaData[] ErrorBatchMeta =
    [
        new SqlMetaData("ExportJobId",        SqlDbType.BigInt),
        new SqlMetaData("ExportWorkerId",     SqlDbType.BigInt),
        new SqlMetaData("DocumentFilePartId", SqlDbType.BigInt),
        new SqlMetaData("VersionPartId",      SqlDbType.BigInt),
        new SqlMetaData("DataFileVersionId",  SqlDbType.BigInt),
        new SqlMetaData("IdempotencyKey",     SqlDbType.Char, 64),
        new SqlMetaData("ErrorSeverity",      SqlDbType.NVarChar, 16),
        new SqlMetaData("ErrorCategory",      SqlDbType.NVarChar, 32),
        new SqlMetaData("ErrorSource",        SqlDbType.NVarChar, 200),
        new SqlMetaData("ExceptionType",      SqlDbType.NVarChar, 400),
        new SqlMetaData("ErrorMessage",       SqlDbType.NVarChar, 4000),
        new SqlMetaData("StackTrace",         SqlDbType.NVarChar, -1), // MAX
        new SqlMetaData("AttemptNumber",      SqlDbType.Int),
        new SqlMetaData("OccurredAtUtc",      SqlDbType.DateTime2, precision: 3, scale: 3),
    ];

    private readonly SqlExecutor _executor;
    private readonly TrackingDatabaseOptions _options;

    public SqlServerErrorRepository(SqlExecutor executor, TrackingDatabaseOptions options)
    {
        _executor = executor;
        _options = options;
    }

    public Task<long> LogAsync(ExportErrorRecord error, CancellationToken cancellationToken)
    {
        return _executor.ExecuteWithConnectionAsync(async (connection, ct) =>
        {
            await using var cmd = new SqlCommand("dbo.usp_LogExportError", connection)
            {
                CommandType    = CommandType.StoredProcedure,
                CommandTimeout = _options.CommandTimeoutSeconds,
            };
            cmd.Parameters.Add(new SqlParameter("@ExportJobId", SqlDbType.BigInt) { Value = error.ExportJobId });
            cmd.Parameters.Add(new SqlParameter("@ExportWorkerId", SqlDbType.BigInt) { Value = (object?)error.ExportWorkerId ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@DocumentFilePartId", SqlDbType.BigInt) { Value = (object?)error.DocumentFilePartId ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@VersionPartId", SqlDbType.BigInt) { Value = (object?)error.VersionPartId ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@DataFileVersionId", SqlDbType.BigInt) { Value = (object?)error.DataFileVersionId ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@IdempotencyKey", SqlDbType.Char, 64) { Value = (object?)error.IdempotencyKeyHex ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@ErrorSeverity", SqlDbType.NVarChar, 16) { Value = error.Severity.ToString() });
            cmd.Parameters.Add(new SqlParameter("@ErrorCategory", SqlDbType.NVarChar, 32) { Value = error.Category.ToString() });
            cmd.Parameters.Add(new SqlParameter("@ErrorSource", SqlDbType.NVarChar, 200) { Value = error.ErrorSource });
            cmd.Parameters.Add(new SqlParameter("@ExceptionType", SqlDbType.NVarChar, 400) { Value = (object?)error.ExceptionType ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@ErrorMessage", SqlDbType.NVarChar, 4000) { Value = error.ErrorMessage });
            cmd.Parameters.Add(new SqlParameter("@StackTrace", SqlDbType.NVarChar, -1) { Value = (object?)error.StackTrace ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@AttemptNumber", SqlDbType.Int) { Value = error.AttemptNumber });
            cmd.Parameters.Add(new SqlParameter("@ActorName", SqlDbType.NVarChar, 200) { Value = ActorContext.Resolve(_options) });
            cmd.Parameters.Add(new SqlParameter("@ActorType", SqlDbType.NVarChar, 32) { Value = "Worker" });
            var outParam = cmd.Parameters.Add(new SqlParameter("@ExportErrorId", SqlDbType.BigInt)
            {
                Direction = ParameterDirection.Output,
            });

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return (long)outParam.Value!;
        }, cancellationToken);
    }

    public Task LogBatchAsync(IReadOnlyCollection<ExportErrorRecord> errors, CancellationToken cancellationToken)
    {
        if (errors.Count == 0) return Task.CompletedTask;

        return _executor.ExecuteNonQueryAsync(
            "dbo.usp_LogExportErrorsBatch",
            cmd =>
            {
                cmd.Parameters.Add(new SqlParameter("@Errors", SqlDbType.Structured)
                {
                    TypeName = "dbo.udt_ExportErrorBatch",
                    Value    = ToTvpRecords(errors),
                });
                cmd.Parameters.Add(new SqlParameter("@ActorName", SqlDbType.NVarChar, 200) { Value = ActorContext.Resolve(_options) });
                cmd.Parameters.Add(new SqlParameter("@ActorType", SqlDbType.NVarChar, 32) { Value = "Worker" });
            },
            cancellationToken);
    }

    public Task ResolveAsync(
        long exportErrorId,
        ExportErrorStatus newStatus,
        string? notes,
        string? actorName,
        CancellationToken cancellationToken)
    {
        return _executor.ExecuteNonQueryAsync(
            "dbo.usp_ResolveExportError",
            cmd =>
            {
                cmd.Parameters.Add(new SqlParameter("@ExportErrorId", SqlDbType.BigInt) { Value = exportErrorId });
                cmd.Parameters.Add(new SqlParameter("@NewStatus", SqlDbType.NVarChar, 32) { Value = newStatus.ToString() });
                cmd.Parameters.Add(new SqlParameter("@ResolutionNotes", SqlDbType.NVarChar, 2000)
                {
                    Value = (object?)notes ?? DBNull.Value,
                });
                cmd.Parameters.Add(new SqlParameter("@ActorName", SqlDbType.NVarChar, 200)
                {
                    Value = actorName ?? ActorContext.Resolve(_options),
                });
                cmd.Parameters.Add(new SqlParameter("@ActorType", SqlDbType.NVarChar, 32) { Value = "User" });
            },
            cancellationToken);
    }

    private static IEnumerable<SqlDataRecord> ToTvpRecords(IReadOnlyCollection<ExportErrorRecord> errors)
    {
        foreach (var e in errors)
        {
            var record = new SqlDataRecord(ErrorBatchMeta);
            record.SetInt64(0, e.ExportJobId);
            if (e.ExportWorkerId is long wid) record.SetInt64(1, wid); else record.SetDBNull(1);
            if (e.DocumentFilePartId is long p) record.SetInt64(2, p); else record.SetDBNull(2);
            if (e.VersionPartId is long v) record.SetInt64(3, v); else record.SetDBNull(3);
            if (e.DataFileVersionId is long d) record.SetInt64(4, d); else record.SetDBNull(4);
            if (e.IdempotencyKeyHex is not null) record.SetString(5, e.IdempotencyKeyHex); else record.SetDBNull(5);
            record.SetString(6, e.Severity.ToString());
            record.SetString(7, e.Category.ToString());
            record.SetString(8, e.ErrorSource);
            if (e.ExceptionType is not null) record.SetString(9, e.ExceptionType); else record.SetDBNull(9);
            record.SetString(10, e.ErrorMessage);
            if (e.StackTrace is not null) record.SetString(11, e.StackTrace); else record.SetDBNull(11);
            record.SetInt32(12, e.AttemptNumber);
            record.SetDateTime(13, e.OccurredAtUtc == default ? DateTime.UtcNow : e.OccurredAtUtc);
            yield return record;
        }
    }
}
