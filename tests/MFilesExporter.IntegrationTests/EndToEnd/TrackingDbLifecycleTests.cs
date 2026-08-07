using FluentAssertions;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.UseCases.Pipeline;
using MFilesExporter.IntegrationTests.Fixtures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace MFilesExporter.IntegrationTests.EndToEnd;

/// <summary>
/// End-to-end coverage of the full <c>RunExportCommand</c> lifecycle —
/// <c>StartExportJob</c> → <c>RegisterWorker</c> → <c>Pipeline</c> →
/// <c>StopWorker</c> → <c>CompleteExportJob</c>. The other integration
/// tests call <c>Pipeline.RunAsync</c> directly and skip this path, so
/// without this test the tracking-DB row wiring would be compiled but
/// never actually exercised.
/// </summary>
[Collection("SqlServer")]
public sealed class TrackingDbLifecycleTests
{
    private const int CorpusSize = 40;

    private readonly SqlServerFixture _sql;

    public TrackingDbLifecycleTests(SqlServerFixture sql) => _sql = sql;

    [Fact]
    public async Task RunExportCommand_PopulatesJob_Worker_AndCheckpointRows()
    {
        // -----------------------------------------------------------------
        // Arrange — clean vault + a Jobs/Workers slate so our assertions are
        // unambiguous. Seed a small corpus.
        // -----------------------------------------------------------------
        await VaultSeeder.ResetAsync(_sql.SourceConnectionString).ConfigureAwait(false);
        await VaultSeeder.SeedAsync(
            _sql.SourceConnectionString,
            CorpusSize,
            seed: 20260810,
            partStartId: 7_000_000L,
            dfvStartId: 11_000_000L).ConfigureAwait(false);

        await using (var setup = new SqlConnection(_sql.TrackingConnectionString))
        {
            await setup.OpenAsync().ConfigureAwait(false);
            await using var wipe = new SqlCommand("""
                DELETE FROM dbo.ExportCheckpoints;
                DELETE FROM dbo.ExportAudit;
                DELETE FROM dbo.ExportWorkers;
                DELETE FROM dbo.ExportJobs;
            """, setup);
            await wipe.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var host = ExporterTestHost.Create(_sql, workerCount: 2, partitionKey: "lifecycle");
        await host.Services.GetRequiredService<IExportStateStore>()
            .InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        // -----------------------------------------------------------------
        // Act — dispatch the full lifecycle command. Uniquely-tagged name
        // so we can find the job row across a shared collection.
        // -----------------------------------------------------------------
        var jobName    = "lifecycle-test-" + Guid.NewGuid().ToString("N")[..8];
        var dispatcher = host.Services.GetRequiredService<IApplicationDispatcher>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await dispatcher.SendAsync<RunExportCommand, RunExportSummary>(
            new RunExportCommand
            {
                JobName        = jobName,
                SourceServer   = "test-vault",
                SourceDatabase = "MFilesVault",
                PartitionKey   = "lifecycle",
                WorkerName     = "test-worker",
                MachineName    = Environment.MachineName,
                Concurrency    = 2,
                ProcessId      = Environment.ProcessId,
            },
            cts.Token).ConfigureAwait(false);

        result.IsSuccess.Should().BeTrue(
            $"the lifecycle command should complete cleanly; errors: {string.Join("; ", result.Errors.Select(e => e.Message))}");
        var summary = result.Value;
        summary.ExportJobId.Should().BeGreaterThan(0, "StartExportJob should return an assigned id");
        summary.ExportWorkerId.Should().BeGreaterThan(0, "RegisterWorker should return an assigned id");

        // -----------------------------------------------------------------
        // Assert — the tracking DB has the rows we expect for this run.
        // -----------------------------------------------------------------
        await using var conn = new SqlConnection(_sql.TrackingConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        // Job row exists with Completed status and the name we set.
        var jobStatus = await ScalarAsync<string>(conn,
            $"SELECT Status FROM dbo.ExportJobs WHERE ExportJobId = {summary.ExportJobId}")
            .ConfigureAwait(false);
        jobStatus.Should().Be("Completed", "the terminal status of a successful run is Completed");

        var jobNameFromDb = await ScalarAsync<string>(conn,
            $"SELECT JobName FROM dbo.ExportJobs WHERE ExportJobId = {summary.ExportJobId}")
            .ConfigureAwait(false);
        jobNameFromDb.Should().Be(jobName);

        // Worker row is Stopped and attached to this job.
        var workerJob = await ScalarAsync<long>(conn,
            $"SELECT ExportJobId FROM dbo.ExportWorkers WHERE ExportWorkerId = {summary.ExportWorkerId}")
            .ConfigureAwait(false);
        workerJob.Should().Be(summary.ExportJobId);

        var workerStatus = await ScalarAsync<string>(conn,
            $"SELECT Status FROM dbo.ExportWorkers WHERE ExportWorkerId = {summary.ExportWorkerId}")
            .ConfigureAwait(false);
        workerStatus.Should().Be("Stopped");

        // Checkpoint row exists for the partition — proves IJobContext was
        // populated and CheckpointEngine's SQL layer actually fired.
        var checkpointRows = await ScalarAsync<int>(conn,
            $"SELECT COUNT(*) FROM dbo.ExportCheckpoints WHERE ExportJobId = {summary.ExportJobId} AND PartitionKey = 'lifecycle'")
            .ConfigureAwait(false);
        checkpointRows.Should().BeGreaterThanOrEqualTo(1,
            "the CheckpointEngine SQL layer should have written at least one row for the job");

        var lastPart = await ScalarAsync<long>(conn,
            $"SELECT MAX(LastDocumentFilePartId) FROM dbo.ExportCheckpoints WHERE ExportJobId = {summary.ExportJobId}")
            .ConfigureAwait(false);
        lastPart.Should().Be(7_000_000L + CorpusSize - 1,
            "the final checkpoint must point at the last seeded document");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static async Task<T> ScalarAsync<T>(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return (T)Convert.ChangeType(result!, typeof(T))!;
    }
}
