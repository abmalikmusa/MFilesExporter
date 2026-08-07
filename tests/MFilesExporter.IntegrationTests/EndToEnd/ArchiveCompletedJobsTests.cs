using FluentAssertions;
using MFilesExporter.IntegrationTests.Fixtures;
using Microsoft.Data.SqlClient;

namespace MFilesExporter.IntegrationTests.EndToEnd;

/// <summary>
/// Exercises <c>ops.usp_ArchiveCompletedJobs</c>. The sproc previously used
/// <c>SELECT j.*</c> into archive tables that don't carry the RowVersion,
/// ModifiedDate, or ModifiedBy columns — a column-count mismatch that
/// killed the sproc at runtime and blocked the archive lifecycle entirely.
/// </summary>
[Collection("SqlServer")]
public sealed class ArchiveCompletedJobsTests
{
    private readonly SqlServerFixture _sql;

    public ArchiveCompletedJobsTests(SqlServerFixture sql) => _sql = sql;

    [Fact]
    public async Task ArchivesCompletedJobRows_ToArchiveSchema_AndPurgesFromDbo()
    {
        // -----------------------------------------------------------------
        // Arrange — insert one Completed job with a couple of child rows,
        // set its CompletedAtUtc far enough in the past that the sproc's
        // default @OlderThanDays filter would ignore it if we didn't
        // override.
        // -----------------------------------------------------------------
        await using var conn = new SqlConnection(_sql.TrackingConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        // Clean slate — remove any job rows other tests left behind so our
        // assertions on archive counts are unambiguous.
        await ExecAsync(conn, """
            DELETE FROM archive.ExportAudit;
            DELETE FROM archive.ExportWorkers;
            DELETE FROM archive.ExportJobs;
            DELETE FROM dbo.ExportAudit;
            DELETE FROM dbo.ExportWorkers;
            DELETE FROM dbo.ExportJobs;
        """).ConfigureAwait(false);

        var jobId = await ScalarAsync<long>(conn, """
            INSERT dbo.ExportJobs (JobName, SourceServer, SourceDatabase, PartitionKey,
                                   Status, StartedAtUtc, CompletedAtUtc)
            VALUES (N'archive-me', N'vault', N'MFilesVault', N'default',
                    N'Completed',
                    DATEADD(DAY, -400, SYSUTCDATETIME()),
                    DATEADD(DAY, -365, SYSUTCDATETIME()));
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
        """).ConfigureAwait(false);

        await ExecAsync(conn, $$"""
            INSERT dbo.ExportWorkers (ExportJobId, WorkerName, MachineName, AssignedPartition,
                                      Concurrency, Status, StoppedAtUtc)
            VALUES ({{jobId}}, N'worker-1', N'host-1', N'default', 4, N'Stopped',
                    DATEADD(DAY, -365, SYSUTCDATETIME()));

            INSERT dbo.ExportAudit (ExportJobId, EntityType, EntityId, AuditAction,
                                    PreviousStatus, NewStatus, ActionDetails)
            VALUES ({{jobId}}, N'ExportJobs', {{jobId}}, N'Updated',
                    N'Running', N'Completed', N'{"reason":"test"}');
        """).ConfigureAwait(false);

        // -----------------------------------------------------------------
        // Act — archive anything older than 30 days.
        // -----------------------------------------------------------------
        await ExecAsync(conn, "EXEC ops.usp_ArchiveCompletedJobs @OlderThanDays = 30, @BatchSize = 100;")
            .ConfigureAwait(false);

        // -----------------------------------------------------------------
        // Assert — dbo rows moved to archive.
        // -----------------------------------------------------------------
        var dboJobs      = await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM dbo.ExportJobs").ConfigureAwait(false);
        var dboWorkers   = await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM dbo.ExportWorkers").ConfigureAwait(false);
        var dboAudit     = await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM dbo.ExportAudit").ConfigureAwait(false);

        var archJobs     = await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM archive.ExportJobs").ConfigureAwait(false);
        var archWorkers  = await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM archive.ExportWorkers").ConfigureAwait(false);
        var archAudit    = await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM archive.ExportAudit").ConfigureAwait(false);

        dboJobs.Should().Be(0,     "the Completed job should have been moved out of dbo");
        dboWorkers.Should().Be(0,  "its worker row should have been moved out of dbo");
        dboAudit.Should().Be(0,    "its audit rows should have been moved out of dbo");

        archJobs.Should().Be(1,    "the Completed job should be visible in archive.ExportJobs");
        archWorkers.Should().Be(1, "its worker row should be in archive.ExportWorkers");
        archAudit.Should().Be(1,   "its audit rows should be in archive.ExportAudit");

        // The archive row should have ArchivedAtUtc set by the DEFAULT.
        var archivedAt = await ScalarAsync<DateTime>(conn,
            $"SELECT ArchivedAtUtc FROM archive.ExportJobs WHERE ExportJobId = {jobId}")
            .ConfigureAwait(false);
        archivedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static async Task ExecAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return (T)Convert.ChangeType(result!, typeof(T))!;
    }
}
