using System.Collections.Concurrent;
using FluentAssertions;
using MFilesExporter.Application.Abstractions.WorkClaiming;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.UseCases.Jobs;
using MFilesExporter.Application.UseCases.Workers;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Domain.WorkClaiming;
using MFilesExporter.IntegrationTests.Fixtures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace MFilesExporter.IntegrationTests.EndToEnd;

/// <summary>
/// Proves the "at-most-once completion under concurrent workers" contract of
/// <see cref="IWorkClaimStore"/>. Enqueues N items, spawns K workers that
/// hammer <c>ClaimAsync</c>+<c>CompleteAsync</c> in parallel, and asserts:
/// <list type="bullet">
///   <item><description>Every worker sees a distinct set of <c>WorkItemId</c>s (no double-claim).</description></item>
///   <item><description>Total completions equals N (nothing lost).</description></item>
///   <item><description>All rows land in <c>Status = 'Completed'</c> (no orphans).</description></item>
/// </list>
/// Everything above the store (batch coordinator, pipeline) is deliberately
/// out of scope — this is the SQL-side atomicity guarantee under load.
/// </summary>
[Collection("SqlServer")]
public sealed class WorkClaimStoreConcurrencyTests
{
    private const int ItemCount    = 200;
    private const int WorkerCount  = 4;
    private const int ClaimBatch   = 8;
    private const string Partition = "workclaim-concurrency";

    private readonly SqlServerFixture _sql;

    public WorkClaimStoreConcurrencyTests(SqlServerFixture sql) => _sql = sql;

    [Fact]
    public async Task ConcurrentWorkers_ClaimEveryItemExactlyOnce()
    {
        // -----------------------------------------------------------------
        // Arrange — wipe the work-item table + job/worker rows so counts are
        // deterministic across shared-container test runs.
        // -----------------------------------------------------------------
        await using (var setup = new SqlConnection(_sql.TrackingConnectionString))
        {
            await setup.OpenAsync().ConfigureAwait(false);
            await using var wipe = new SqlCommand("""
                DELETE FROM dbo.ExportWorkItems;
                DELETE FROM dbo.ExportWorkers;
                DELETE FROM dbo.ExportJobs;
            """, setup);
            await wipe.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var host = ExporterTestHost.Create(_sql, workerCount: WorkerCount, partitionKey: Partition);

        var dispatcher = host.Services.GetRequiredService<IApplicationDispatcher>();
        var store      = host.Services.GetRequiredService<IWorkClaimStore>();

        // Create the job + worker rows the FK constraints require.
        var jobResult = await dispatcher.SendAsync<StartExportJobCommand, long>(
            new StartExportJobCommand
            {
                JobName        = "workclaim-conc-" + Guid.NewGuid().ToString("N")[..8],
                SourceServer   = "test-vault",
                SourceDatabase = "MFilesVault",
                PartitionKey   = Partition,
            },
            CancellationToken.None).ConfigureAwait(false);
        jobResult.IsSuccess.Should().BeTrue();
        var jobId = jobResult.Value;

        var workerIds = new long[WorkerCount];
        for (var w = 0; w < WorkerCount; w++)
        {
            var reg = await dispatcher.SendAsync<RegisterWorkerCommand, long>(
                new RegisterWorkerCommand
                {
                    ExportJobId       = jobId,
                    WorkerName        = $"worker-{w}",
                    MachineName       = Environment.MachineName,
                    ProcessId         = Environment.ProcessId,
                    AssignedPartition = Partition,
                    Concurrency       = 1,
                },
                CancellationToken.None).ConfigureAwait(false);
            reg.IsSuccess.Should().BeTrue();
            workerIds[w] = reg.Value;
        }

        // Enqueue ItemCount work items with distinct idempotency keys.
        var enqueueRequests = Enumerable.Range(1, ItemCount)
            .Select(i => new WorkItemEnqueueRequest
            {
                DocumentFileVersionKey = new DocumentFileVersionKey(5_000_000L + i, 1),
                DataFileVersionKey     = new DataFileVersionKey(5_000_000L + i, 20_000_000L + i),
                IdempotencyKey         = IdempotencyKey.For(5_000_000L + i, 1, 20_000_000L + i),
                Priority               = 0,
                MaxAttempts            = 3,
            })
            .ToArray();

        var enqueued = await store.EnqueueAsync(jobId, enqueueRequests, CancellationToken.None)
            .ConfigureAwait(false);
        enqueued.Should().Be(ItemCount, "the enqueue must insert one row per unique idempotency key");

        // -----------------------------------------------------------------
        // Act — K workers concurrently claim + complete until exhausted.
        // -----------------------------------------------------------------
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        // Barrier ensures all workers start their first ClaimAsync at (near-)
        // exactly the same instant, maximising the chance of contention on
        // the claim UPDATE.
        using var barrier = new Barrier(WorkerCount);
        var claimedIdsPerWorker = new ConcurrentBag<(long WorkerId, long WorkItemId)>();
        var completionResults   = new ConcurrentBag<bool>();

        async Task WorkerLoop(long workerId)
        {
            barrier.SignalAndWait(cts.Token);
            while (!cts.IsCancellationRequested)
            {
                var claimed = await store.ClaimAsync(
                    jobId, workerId, ClaimBatch, TimeSpan.FromMinutes(1), cts.Token)
                    .ConfigureAwait(false);

                if (claimed.Count == 0) return;

                foreach (var item in claimed)
                {
                    claimedIdsPerWorker.Add((workerId, item.WorkItemId.Value));
                    var ok = await store.CompleteAsync(
                        item.WorkItemId, item.ClaimToken,
                        outputPath:   $"/fake/worker-{workerId}/{item.WorkItemId.Value}.bin",
                        checksum:     new string('a', 64),
                        bytesWritten: 1024L,
                        cts.Token).ConfigureAwait(false);
                    completionResults.Add(ok);
                }
            }
        }

        // Task.Run so each worker starts on its own pool thread — otherwise
        // the barrier's SignalAndWait would deadlock the synchronous
        // enumeration before the second worker gets to signal.
        var workerTasks = workerIds.Select(id => Task.Run(() => WorkerLoop(id), cts.Token)).ToArray();
        await Task.WhenAll(workerTasks).ConfigureAwait(false);

        // -----------------------------------------------------------------
        // Assert — the store's atomicity contract holds.
        // -----------------------------------------------------------------
        var allClaimed = claimedIdsPerWorker.ToArray();
        allClaimed.Should().HaveCount(ItemCount,
            "every item must be claimed exactly once — total claims across workers must equal the enqueued count");

        allClaimed.Select(x => x.WorkItemId).Distinct().Count().Should().Be(ItemCount,
            "no two workers may have claimed the same WorkItemId — that would break the at-most-once invariant");

        completionResults.Should().OnlyContain(ok => ok,
            "every CompleteAsync must return true because the calling worker still owns the lease");

        // Verify each worker actually did some work (no starving under barrier-synced start).
        var workerContribution = allClaimed.GroupBy(x => x.WorkerId).ToDictionary(g => g.Key, g => g.Count());
        workerContribution.Should().HaveCount(WorkerCount,
            "every worker should have claimed at least one batch under this load");

        // Verify SQL-side terminal state.
        await using var verify = new SqlConnection(_sql.TrackingConnectionString);
        await verify.OpenAsync().ConfigureAwait(false);

        var completedRows = await ScalarAsync<int>(verify,
            $"SELECT COUNT(*) FROM dbo.ExportWorkItems WHERE ExportJobId = {jobId} AND Status = 'Completed'")
            .ConfigureAwait(false);
        completedRows.Should().Be(ItemCount, "every row must land in the Completed terminal state");

        var nonCompletedRows = await ScalarAsync<int>(verify,
            $"SELECT COUNT(*) FROM dbo.ExportWorkItems WHERE ExportJobId = {jobId} AND Status <> 'Completed'")
            .ConfigureAwait(false);
        nonCompletedRows.Should().Be(0, "no row should be left Available/Claimed/Failed/DeadLettered");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static async Task<T> ScalarAsync<T>(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return (T)Convert.ChangeType(result!, typeof(T))!;
    }
}
