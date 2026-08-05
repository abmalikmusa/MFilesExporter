using System.Data;
using Microsoft.Data.SqlClient.Server;
using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Models.Tracking;
using MFilesExporter.Configuration.Options;
using Microsoft.Data.SqlClient;

namespace MFilesExporter.Persistence.Tracking.Sql;

/// <summary>
/// Repository for the ExportMetrics table.
///
/// The batch path uses a TVP streamed as <see cref="IEnumerable{SqlDataRecord}"/>
/// — no DataTable is ever allocated, and rows flow directly from client memory
/// to the server on the same wire as the RPC. Throughput on this path
/// commonly exceeds 100K rows/sec on a well-configured connection.
/// </summary>
internal sealed class SqlServerMetricRepository : IExportMetricRepository
{
    private static readonly SqlMetaData[] MetricBatchMeta =
    [
        new SqlMetaData("ExportJobId",    SqlDbType.BigInt),
        new SqlMetaData("ExportWorkerId", SqlDbType.BigInt),
        new SqlMetaData("MetricName",     SqlDbType.NVarChar, 200),
        new SqlMetaData("MetricValue",    SqlDbType.Float),
        new SqlMetaData("MetricUnit",     SqlDbType.NVarChar, 50),
        new SqlMetaData("Tags",           SqlDbType.NVarChar, 2000),
        new SqlMetaData("CapturedAtUtc",  SqlDbType.DateTime2, precision: 3, scale: 3),
    ];

    private readonly SqlExecutor _executor;
    private readonly TrackingDatabaseOptions _options;

    public SqlServerMetricRepository(SqlExecutor executor, TrackingDatabaseOptions options)
    {
        _executor = executor;
        _options = options;
    }

    public Task RecordAsync(ExportMetricRecord metric, CancellationToken cancellationToken)
    {
        return _executor.ExecuteNonQueryAsync(
            "dbo.usp_RecordExportMetric",
            cmd =>
            {
                cmd.Parameters.Add(new SqlParameter("@ExportJobId", SqlDbType.BigInt) { Value = metric.ExportJobId });
                cmd.Parameters.Add(new SqlParameter("@ExportWorkerId", SqlDbType.BigInt)
                {
                    Value = (object?)metric.ExportWorkerId ?? DBNull.Value,
                });
                cmd.Parameters.Add(new SqlParameter("@MetricName", SqlDbType.NVarChar, 200) { Value = metric.MetricName });
                cmd.Parameters.Add(new SqlParameter("@MetricValue", SqlDbType.Float) { Value = metric.MetricValue });
                cmd.Parameters.Add(new SqlParameter("@MetricUnit", SqlDbType.NVarChar, 50) { Value = metric.MetricUnit });
                cmd.Parameters.Add(new SqlParameter("@Tags", SqlDbType.NVarChar, 2000)
                {
                    Value = (object?)metric.TagsJson ?? DBNull.Value,
                });
                cmd.Parameters.Add(new SqlParameter("@ActorName", SqlDbType.NVarChar, 200) { Value = ActorContext.Resolve(_options) });
            },
            cancellationToken);
    }

    public Task RecordBatchAsync(IReadOnlyCollection<ExportMetricRecord> metrics, CancellationToken cancellationToken)
    {
        if (metrics.Count == 0) return Task.CompletedTask;

        return _executor.ExecuteNonQueryAsync(
            "dbo.usp_RecordExportMetricsBatch",
            cmd =>
            {
                cmd.Parameters.Add(new SqlParameter("@Metrics", SqlDbType.Structured)
                {
                    TypeName = "dbo.udt_ExportMetricBatch",
                    Value    = ToTvpRecords(metrics),
                });
                cmd.Parameters.Add(new SqlParameter("@ActorName", SqlDbType.NVarChar, 200) { Value = ActorContext.Resolve(_options) });
            },
            cancellationToken);
    }

    private static IEnumerable<SqlDataRecord> ToTvpRecords(IReadOnlyCollection<ExportMetricRecord> metrics)
    {
        foreach (var m in metrics)
        {
            var record = new SqlDataRecord(MetricBatchMeta);
            record.SetInt64(0, m.ExportJobId);
            if (m.ExportWorkerId is long wid) record.SetInt64(1, wid); else record.SetDBNull(1);
            record.SetString(2, m.MetricName);
            record.SetDouble(3, m.MetricValue);
            record.SetString(4, m.MetricUnit ?? string.Empty);
            if (m.TagsJson is not null) record.SetString(5, m.TagsJson); else record.SetDBNull(5);
            record.SetDateTime(6, m.CapturedAtUtc == default ? DateTime.UtcNow : m.CapturedAtUtc);
            yield return record;
        }
    }
}
