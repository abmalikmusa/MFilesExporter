using System.Data;
using System.Runtime.CompilerServices;
using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Models.Tracking;
using MFilesExporter.Configuration.Options;
using Microsoft.Data.SqlClient;

namespace MFilesExporter.Persistence.Tracking.Sql;

internal sealed class SqlServerAuditRepository : IExportAuditRepository
{
    private readonly ITrackingSqlConnectionFactory _connectionFactory;
    private readonly TrackingDatabaseOptions _options;

    public SqlServerAuditRepository(ITrackingSqlConnectionFactory connectionFactory, TrackingDatabaseOptions options)
    {
        _connectionFactory = connectionFactory;
        _options = options;
    }

    public async IAsyncEnumerable<ExportAuditRecord> ReadRecentAsync(
        long exportJobId,
        int take,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (take <= 0) yield break;

        const string sql = @"
            SELECT TOP (@Take)
                ExportAuditId, ExportJobId, EntityType, EntityId,
                AuditAction, PreviousStatus, NewStatus, ActionDetails,
                ActorName, ActorType, OccurredAtUtc
            FROM dbo.ExportAudit
            WHERE ExportJobId = @ExportJobId
            ORDER BY OccurredAtUtc DESC, ExportAuditId DESC;";

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, connection)
        {
            CommandType    = CommandType.Text,
            CommandTimeout = _options.CommandTimeoutSeconds,
        };
        cmd.Parameters.Add(new SqlParameter("@ExportJobId", SqlDbType.BigInt) { Value = exportJobId });
        cmd.Parameters.Add(new SqlParameter("@Take", SqlDbType.Int) { Value = take });

        await using var reader = await cmd.ExecuteReaderAsync(
            CommandBehavior.SingleResult,
            cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new ExportAuditRecord(
                ExportAuditId:      reader.GetInt64(0),
                ExportJobId:        await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(1),
                EntityType:         reader.GetString(2),
                EntityId:           reader.GetInt64(3),
                AuditAction:        reader.GetString(4),
                PreviousStatus:     await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
                NewStatus:          await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
                ActionDetailsJson:  await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7),
                ActorName:          reader.GetString(8),
                ActorType:          Enum.Parse<ExportAuditActor>(reader.GetString(9)),
                OccurredAtUtc:      DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc));
        }
    }
}
