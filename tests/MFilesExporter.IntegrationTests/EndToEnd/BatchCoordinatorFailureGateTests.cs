using FluentAssertions;
using MFilesExporter.Application.Abstractions.WorkClaiming;
using MFilesExporter.Application.Batching;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.UseCases.Jobs;
using MFilesExporter.Application.UseCases.Workers;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Domain.WorkClaiming;
using MFilesExporter.IntegrationTests.Fixtures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace MFilesExporter.IntegrationTests.EndToEnd;

/// <summary>
/// End-to-end coverage of <see cref="BatchProcessingOptions.FailureRateThreshold"/>.
/// The <see cref="SequentialBatchCoordinator"/> is unit-tested against an
/// in-memory source and a fake processor; this test drives it against the
/// real <see cref="WorkClaimBatchSource"/> + <see cref="IWorkClaimStore"/>
/// with a processor that fails every item, and asserts:
/// <list type="bullet">
///   <item><description>The coordinator aborts on the first batch when the failure ratio blows past the threshold.</description></item>
///   <item><description>Only <see cref="BatchProcessingOptions.BatchSize"/> items were claimed — the remainder is untouched in the DB.</description></item>
/// </list>
/// This is the missing "real DB, real store, real coordinator" integration
/// that proves the failure-rate gate actually short-circuits a live run.
/// </summary>
[Collection("SqlServer")]
public sealed class BatchCoordinatorFailureGateTests
{
    private const int EnqueuedItems = 100;
    private const int BatchSize     = 20;   // → coordinator processes exactly one batch before aborting
    private const string Partition  = "batchgate";

    private readonly SqlServerFixture _sql;

    public BatchCoordinatorFailureGateTests(SqlServerFixture sql) => _sql = sql;

    [Fact]
    public async Task FailureRateThreshold_Trips_OnFirstBatch_AndLeavesRemainingItemsUnclaimed()
    {
        // -----------------------------------------------------------------
        // Arrange — wipe the tracking DB slate, boot a host with our batch
        // options, create job + worker rows, enqueue work items.
        // -----------------------------------------------------------------
        await using (var setup = new SqlConnection(_sql.TrackingConnectionString))
        {
            await setup.OpenAsync().ConfigureAwait(false);
            await using var wipe = new SqlCommand("""
                DELETE FROM dbo.ExportCheckpoints;
                DELETE FROM dbo.ExportAudit;
                DELETE FROM dbo.ExportWorkItems;
                DELETE FROM dbo.ExportWorkers;
                DELETE FROM dbo.ExportJobs;
            """, setup);
            await wipe.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var host = ExporterTestHost.Create(
            _sql,
            workerCount: 1,
            partitionKey: Partition,
            customize: services =>
            {
                services.PostConfigure<ExporterOptions>(o =>
                {
                    o.BatchProcessing.BatchSize              = BatchSize;
                    o.BatchProcessing.MaxParallelismPerBatch = 4;
                    o.BatchProcessing.FailureRateThreshold   = 0.3;  // 30 % — a fully-failing batch trips this
                });
            });

        var dispatcher = host.Services.GetRequiredService<IApplicationDispatcher>();
        var store      = host.Services.GetRequiredService<IWorkClaimStore>();
        var source     = host.Services.GetRequiredService<IBatchSource<ClaimedWorkItem>>();
        var coordinator= host.Services.GetRequiredService<IBatchCoordinator>();

        var jobResult = await dispatcher.SendAsync<StartExportJobCommand, long>(
            new StartExportJobCommand
            {
                JobName        = "batchgate-" + Guid.NewGuid().ToString("N")[..8],
                SourceServer   = "test-vault",
                SourceDatabase = "MFilesVault",
                PartitionKey   = Partition,
            },
            CancellationToken.None).ConfigureAwait(false);
        jobResult.IsSuccess.Should().BeTrue();
        var jobId = jobResult.Value;

        var workerResult = await dispatcher.SendAsync<RegisterWorkerCommand, long>(
            new RegisterWorkerCommand
            {
                ExportJobId       = jobId,
                WorkerName        = "batchgate-worker",
                MachineName       = Environment.MachineName,
                ProcessId         = Environment.ProcessId,
                AssignedPartition = Partition,
                Concurrency       = 1,
            },
            CancellationToken.None).ConfigureAwait(false);
        workerResult.IsSuccess.Should().BeTrue();
        var workerId = workerResult.Value;

        var enqueueRequests = Enumerable.Range(1, EnqueuedItems)
            .Select(i => new WorkItemEnqueueRequest
            {
                DocumentFileVersionKey = new DocumentFileVersionKey(13_000_000L + i, 1),
                DataFileVersionKey     = new DataFileVersionKey(13_000_000L + i, 17_000_000L + i),
                IdempotencyKey         = IdempotencyKey.For(13_000_000L + i, 1, 17_000_000L + i),
                Priority               = 0,
                MaxAttempts            = 3,
            })
            .ToArray();
        var enqueued = await store.EnqueueAsync(jobId, enqueueRequests, CancellationToken.None)
            .ConfigureAwait(false);
        enqueued.Should().Be(EnqueuedItems);

        // -----------------------------------------------------------------
        // Act — run the coordinator against a processor that fails every
        // item. The first batch's failure rate is 100 % — well above 30 %.
        // -----------------------------------------------------------------
        var processor = new AlwaysFailingItemProcessor(store);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        var summary = await coordinator.RunAsync(
            source,
            processor,
            new BatchContext
            {
                ExportJobId   = jobId,
                WorkerId      = workerId,
                PartitionKey  = Partition,
                CorrelationId = CorrelationId.New(),
            },
            cts.Token).ConfigureAwait(false);

        // -----------------------------------------------------------------
        // Assert — coordinator aborted on the first batch; the remaining
        // items are still Available in the tracking DB.
        // -----------------------------------------------------------------
        summary.AbortedOnThreshold.Should().BeTrue(
            "a 100 % failure ratio must trip the 30 % gate on the first batch");
        summary.TotalBatches.Should().Be(1,
            "only the first batch should have been processed before the abort");
        summary.TotalItems.Should().Be(BatchSize,
            "every item in the first batch must have been touched (attempted + failed)");
        summary.TotalSucceeded.Should().Be(0);
        summary.TotalFailed.Should().Be(BatchSize);
        summary.ExhaustedSource.Should().BeFalse(
            "the source is still hot — the abort must be reflected in ExhaustedSource=false");
        processor.CallCount.Should().Be(BatchSize);

        await using var verify = new SqlConnection(_sql.TrackingConnectionString);
        await verify.OpenAsync().ConfigureAwait(false);

        // The abort proof: only rows in the first batch got their attempt
        // counter bumped. A transient FailAsync returns the row to Available,
        // so a "Status = Available" count doesn't distinguish touched vs
        // untouched — AttemptCount does.
        var touchedRows = await ScalarAsync<int>(verify,
            $"SELECT COUNT(*) FROM dbo.ExportWorkItems WHERE ExportJobId = {jobId} AND AttemptCount > 0")
            .ConfigureAwait(false);
        touchedRows.Should().Be(BatchSize,
            "only the first batch was claimed and failed — the abort must have prevented any further claim");

        var untouchedRows = await ScalarAsync<int>(verify,
            $"SELECT COUNT(*) FROM dbo.ExportWorkItems WHERE ExportJobId = {jobId} AND AttemptCount = 0")
            .ConfigureAwait(false);
        untouchedRows.Should().Be(EnqueuedItems - BatchSize,
            "the batches that would have been processed post-abort must be pristine");

        var completedRows = await ScalarAsync<int>(verify,
            $"SELECT COUNT(*) FROM dbo.ExportWorkItems WHERE ExportJobId = {jobId} AND Status = N'Completed'")
            .ConfigureAwait(false);
        completedRows.Should().Be(0, "no item was completed because the processor always fails");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Processor that fails every item and records the failure on the store
    /// so the claim is released (transient) and doesn't leak.
    /// </summary>
    private sealed class AlwaysFailingItemProcessor : IBatchItemProcessor<ClaimedWorkItem>
    {
        private readonly IWorkClaimStore _store;
        private int _callCount;

        public AlwaysFailingItemProcessor(IWorkClaimStore store) => _store = store;

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<BatchItemResult> ProcessAsync(
            ClaimedWorkItem item,
            BatchContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            await _store.FailAsync(
                item.WorkItemId, item.ClaimToken,
                reason: "simulated processor failure",
                isPermanent: false,
                backoff: TimeSpan.FromMinutes(5),  // long enough that the abort test window sees these as unavailable
                cancellationToken).ConfigureAwait(false);
            return BatchItemResult.Failed("simulated");
        }
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return (T)Convert.ChangeType(result!, typeof(T))!;
    }
}
