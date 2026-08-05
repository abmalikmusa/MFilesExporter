using System.Data;
using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Models.Tracking;
using MFilesExporter.Configuration.Options;
using Microsoft.Data.SqlClient;

namespace MFilesExporter.Persistence.Tracking.Sql;

internal sealed class SqlServerCheckpointRepository : IExportCheckpointRepository
{
    private readonly SqlExecutor _executor;
    private readonly TrackingDatabaseOptions _options;

    public SqlServerCheckpointRepository(SqlExecutor executor, TrackingDatabaseOptions options)
    {
        _executor = executor;
        _options = options;
    }

    public Task<bool> SaveAsync(
        long exportJobId,
        string partitionKey,
        long lastDocumentFilePartId,
        long lastVersionPartId,
        long? documentsProcessedInPartition,
        CancellationToken cancellationToken)
    {
        return _executor.ExecuteWithConnectionAsync(async (connection, ct) =>
        {
            await using var cmd = new SqlCommand("dbo.usp_SaveExportCheckpoint", connection)
            {
                CommandType    = CommandType.StoredProcedure,
                CommandTimeout = _options.CommandTimeoutSeconds,
            };
            cmd.Parameters.Add(new SqlParameter("@ExportJobId", SqlDbType.BigInt) { Value = exportJobId });
            cmd.Parameters.Add(new SqlParameter("@PartitionKey", SqlDbType.NVarChar, 100) { Value = partitionKey });
            cmd.Parameters.Add(new SqlParameter("@LastDocumentFilePartId", SqlDbType.BigInt) { Value = lastDocumentFilePartId });
            cmd.Parameters.Add(new SqlParameter("@LastVersionPartId", SqlDbType.BigInt) { Value = lastVersionPartId });
            cmd.Parameters.Add(new SqlParameter("@DocumentsProcessedInPartition", SqlDbType.BigInt)
            {
                Value = (object?)documentsProcessedInPartition ?? DBNull.Value,
            });
            cmd.Parameters.Add(new SqlParameter("@ActorName", SqlDbType.NVarChar, 200) { Value = ActorContext.Resolve(_options) });
            cmd.Parameters.Add(new SqlParameter("@ActorType", SqlDbType.NVarChar, 32) { Value = "Worker" });

            var advancedParam = cmd.Parameters.Add(new SqlParameter("@Advanced", SqlDbType.Bit)
            {
                Direction = ParameterDirection.Output,
            });

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return advancedParam.Value is bool b && b;
        }, cancellationToken);
    }

    public Task<ExportCheckpointRecord?> GetActiveAsync(
        long exportJobId,
        string partitionKey,
        CancellationToken cancellationToken)
    {
        return _executor.ExecuteWithConnectionAsync<ExportCheckpointRecord?>(async (connection, ct) =>
        {
            await using var cmd = new SqlCommand("dbo.usp_GetLatestCheckpoint", connection)
            {
                CommandType    = CommandType.StoredProcedure,
                CommandTimeout = _options.CommandTimeoutSeconds,
            };
            cmd.Parameters.Add(new SqlParameter("@ExportJobId", SqlDbType.BigInt) { Value = exportJobId });
            cmd.Parameters.Add(new SqlParameter("@PartitionKey", SqlDbType.NVarChar, 100) { Value = partitionKey });

            await using var reader = await cmd.ExecuteReaderAsync(
                CommandBehavior.SingleResult | CommandBehavior.SingleRow,
                ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new ExportCheckpointRecord(
                ExportCheckpointId:            reader.GetInt64(0),
                ExportJobId:                   reader.GetInt64(1),
                PartitionKey:                  reader.GetString(2),
                LastDocumentFilePartId:        reader.GetInt64(3),
                LastVersionPartId:             reader.GetInt64(4),
                DocumentsProcessedInPartition: await reader.IsDBNullAsync(5, ct).ConfigureAwait(false) ? null : reader.GetInt64(5),
                CheckpointAtUtc:               DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
                Status:                        MapStatus(reader.GetString(7)));
        }, cancellationToken);
    }

    private static ExportCheckpointStatus MapStatus(string s) => s switch
    {
        "Active"      => ExportCheckpointStatus.Active,
        "Superseded"  => ExportCheckpointStatus.Superseded,
        "Rolled Back" => ExportCheckpointStatus.RolledBack,
        "Archived"    => ExportCheckpointStatus.Archived,
        _             => throw new InvalidOperationException($"Unknown checkpoint status '{s}'."),
    };
}
