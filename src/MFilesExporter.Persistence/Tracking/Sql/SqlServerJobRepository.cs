using System.Data;
using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Models.Tracking;
using MFilesExporter.Configuration.Options;
using Microsoft.Data.SqlClient;

namespace MFilesExporter.Persistence.Tracking.Sql;

internal sealed class SqlServerJobRepository : IExportJobRepository
{
    private readonly SqlExecutor _executor;
    private readonly TrackingDatabaseOptions _options;

    public SqlServerJobRepository(SqlExecutor executor, TrackingDatabaseOptions options)
    {
        _executor = executor;
        _options = options;
    }

    public Task<long> StartAsync(
        string jobName,
        string sourceServer,
        string sourceDatabase,
        string partitionKey,
        long? totalDocumentsExpected,
        CancellationToken cancellationToken)
    {
        return _executor.ExecuteWithConnectionAsync(async (connection, ct) =>
        {
            await using var cmd = new SqlCommand("dbo.usp_StartExportJob", connection)
            {
                CommandType    = CommandType.StoredProcedure,
                CommandTimeout = _options.CommandTimeoutSeconds,
            };
            cmd.Parameters.Add(new SqlParameter("@JobName", SqlDbType.NVarChar, 200) { Value = jobName });
            cmd.Parameters.Add(new SqlParameter("@SourceServer", SqlDbType.NVarChar, 256) { Value = sourceServer });
            cmd.Parameters.Add(new SqlParameter("@SourceDatabase", SqlDbType.NVarChar, 256) { Value = sourceDatabase });
            cmd.Parameters.Add(new SqlParameter("@PartitionKey", SqlDbType.NVarChar, 100) { Value = partitionKey });
            cmd.Parameters.Add(new SqlParameter("@TotalDocumentsExpected", SqlDbType.BigInt)
            {
                Value = (object?)totalDocumentsExpected ?? DBNull.Value,
            });
            cmd.Parameters.Add(new SqlParameter("@ActorName", SqlDbType.NVarChar, 200) { Value = ActorContext.Resolve(_options) });
            cmd.Parameters.Add(new SqlParameter("@ActorType", SqlDbType.NVarChar, 32) { Value = "Service" });

            var outParam = cmd.Parameters.Add(new SqlParameter("@ExportJobId", SqlDbType.BigInt)
            {
                Direction = ParameterDirection.Output,
            });

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return (long)outParam.Value!;
        }, cancellationToken);
    }

    public Task CompleteAsync(
        long exportJobId,
        ExportJobStatus terminalStatus,
        string? reason,
        CancellationToken cancellationToken)
    {
        return _executor.ExecuteNonQueryAsync(
            "dbo.usp_CompleteExportJob",
            cmd =>
            {
                cmd.Parameters.Add(new SqlParameter("@ExportJobId", SqlDbType.BigInt) { Value = exportJobId });
                cmd.Parameters.Add(new SqlParameter("@TerminalStatus", SqlDbType.NVarChar, 32) { Value = terminalStatus.ToString() });
                cmd.Parameters.Add(new SqlParameter("@Reason", SqlDbType.NVarChar, 2000)
                {
                    Value = (object?)reason ?? DBNull.Value,
                });
                cmd.Parameters.Add(new SqlParameter("@ActorName", SqlDbType.NVarChar, 200) { Value = ActorContext.Resolve(_options) });
                cmd.Parameters.Add(new SqlParameter("@ActorType", SqlDbType.NVarChar, 32) { Value = "Service" });
            },
            cancellationToken);
    }

    public Task<ExportJobRecord?> GetAsync(long exportJobId, CancellationToken cancellationToken)
    {
        return _executor.ExecuteWithConnectionAsync<ExportJobRecord?>(async (connection, ct) =>
        {
            const string sql = @"
                SELECT ExportJobId, JobName, SourceServer, SourceDatabase, PartitionKey,
                       TotalDocumentsExpected, StartedAtUtc, CompletedAtUtc, CancellationReason,
                       Status, CreatedDate, CreatedBy, ModifiedDate, ModifiedBy
                FROM dbo.ExportJobs
                WHERE ExportJobId = @ExportJobId;";

            await using var cmd = new SqlCommand(sql, connection)
            {
                CommandType    = CommandType.Text,
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

            return new ExportJobRecord(
                ExportJobId:            reader.GetInt64(0),
                JobName:                reader.GetString(1),
                SourceServer:           reader.GetString(2),
                SourceDatabase:         reader.GetString(3),
                PartitionKey:           reader.GetString(4),
                TotalDocumentsExpected: await reader.IsDBNullAsync(5, ct).ConfigureAwait(false) ? null : reader.GetInt64(5),
                StartedAtUtc:           await reader.IsDBNullAsync(6, ct).ConfigureAwait(false) ? null : DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
                CompletedAtUtc:         await reader.IsDBNullAsync(7, ct).ConfigureAwait(false) ? null : DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
                CancellationReason:     await reader.IsDBNullAsync(8, ct).ConfigureAwait(false) ? null : reader.GetString(8),
                Status:                 Enum.Parse<ExportJobStatus>(reader.GetString(9)),
                CreatedDate:            DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc),
                CreatedBy:              reader.GetString(11),
                ModifiedDate:           DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc),
                ModifiedBy:             reader.GetString(13));
        }, cancellationToken);
    }
}
