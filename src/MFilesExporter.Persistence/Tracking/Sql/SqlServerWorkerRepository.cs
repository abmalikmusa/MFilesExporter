using System.Data;
using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Models.Tracking;
using MFilesExporter.Configuration.Options;
using Microsoft.Data.SqlClient;

namespace MFilesExporter.Persistence.Tracking.Sql;

internal sealed class SqlServerWorkerRepository : IExportWorkerRepository
{
    private readonly SqlExecutor _executor;
    private readonly TrackingDatabaseOptions _options;

    public SqlServerWorkerRepository(SqlExecutor executor, TrackingDatabaseOptions options)
    {
        _executor = executor;
        _options = options;
    }

    public Task<long> RegisterAsync(
        long exportJobId,
        string workerName,
        string machineName,
        int? processId,
        string assignedPartition,
        int concurrency,
        CancellationToken cancellationToken)
    {
        return _executor.ExecuteWithConnectionAsync(async (connection, ct) =>
        {
            await using var cmd = new SqlCommand("dbo.usp_RegisterExportWorker", connection)
            {
                CommandType    = CommandType.StoredProcedure,
                CommandTimeout = _options.CommandTimeoutSeconds,
            };
            cmd.Parameters.Add(new SqlParameter("@ExportJobId", SqlDbType.BigInt) { Value = exportJobId });
            cmd.Parameters.Add(new SqlParameter("@WorkerName", SqlDbType.NVarChar, 200) { Value = workerName });
            cmd.Parameters.Add(new SqlParameter("@MachineName", SqlDbType.NVarChar, 200) { Value = machineName });
            cmd.Parameters.Add(new SqlParameter("@ProcessId", SqlDbType.Int)
            {
                Value = (object?)processId ?? DBNull.Value,
            });
            cmd.Parameters.Add(new SqlParameter("@AssignedPartition", SqlDbType.NVarChar, 100) { Value = assignedPartition });
            cmd.Parameters.Add(new SqlParameter("@Concurrency", SqlDbType.Int) { Value = concurrency });
            cmd.Parameters.Add(new SqlParameter("@ActorName", SqlDbType.NVarChar, 200) { Value = ActorContext.Resolve(_options) });
            cmd.Parameters.Add(new SqlParameter("@ActorType", SqlDbType.NVarChar, 32) { Value = "Worker" });

            var outParam = cmd.Parameters.Add(new SqlParameter("@ExportWorkerId", SqlDbType.BigInt)
            {
                Direction = ParameterDirection.Output,
            });

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return (long)outParam.Value!;
        }, cancellationToken);
    }

    public Task HeartbeatAsync(long exportWorkerId, ExportWorkerStatus status, CancellationToken cancellationToken)
    {
        return _executor.ExecuteNonQueryAsync(
            "dbo.usp_HeartbeatExportWorker",
            cmd =>
            {
                cmd.Parameters.Add(new SqlParameter("@ExportWorkerId", SqlDbType.BigInt) { Value = exportWorkerId });
                cmd.Parameters.Add(new SqlParameter("@NewStatus", SqlDbType.NVarChar, 32) { Value = status.ToString() });
            },
            cancellationToken);
    }

    public Task StopAsync(long exportWorkerId, string? reason, CancellationToken cancellationToken)
    {
        return _executor.ExecuteNonQueryAsync(
            "dbo.usp_StopExportWorker",
            cmd =>
            {
                cmd.Parameters.Add(new SqlParameter("@ExportWorkerId", SqlDbType.BigInt) { Value = exportWorkerId });
                cmd.Parameters.Add(new SqlParameter("@Reason", SqlDbType.NVarChar, 1000)
                {
                    Value = (object?)reason ?? DBNull.Value,
                });
                cmd.Parameters.Add(new SqlParameter("@ActorName", SqlDbType.NVarChar, 200) { Value = ActorContext.Resolve(_options) });
                cmd.Parameters.Add(new SqlParameter("@ActorType", SqlDbType.NVarChar, 32) { Value = "Worker" });
            },
            cancellationToken);
    }
}
