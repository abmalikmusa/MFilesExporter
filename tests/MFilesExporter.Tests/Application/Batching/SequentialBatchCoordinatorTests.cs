using System.Runtime.CompilerServices;
using FluentAssertions;
using MFilesExporter.Application.Batching;
using MFilesExporter.Application.Common;
using MFilesExporter.Configuration.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace MFilesExporter.Tests.Application.Batching;

public class SequentialBatchCoordinatorTests
{
    private static BatchContext Ctx() => new()
    {
        ExportJobId = 1, WorkerId = 1, PartitionKey = "p",
        CorrelationId = CorrelationId.New(),
    };

    private sealed class InMemorySource : IBatchSource<int>
    {
        public required IReadOnlyList<Batch<int>> Batches { get; init; }
        public int Emitted { get; private set; }

        public async IAsyncEnumerable<Batch<int>> ReadBatchesAsync(
            BatchContext context,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var b in Batches)
            {
                ct.ThrowIfCancellationRequested();
                Emitted++;
                yield return b;
                await Task.Yield();
            }
        }
    }

    private sealed class SucceedingProcessor : IBatchItemProcessor<int>
    {
        public Task<BatchItemResult> ProcessAsync(int item, BatchContext ctx, CancellationToken ct) =>
            Task.FromResult(BatchItemResult.Succeeded(1));
    }

    private sealed class HalfFailingProcessor : IBatchItemProcessor<int>
    {
        public Task<BatchItemResult> ProcessAsync(int item, BatchContext ctx, CancellationToken ct) =>
            Task.FromResult(item % 2 == 0 ? BatchItemResult.Succeeded(1) : BatchItemResult.Failed("bad"));
    }

    private static Batch<int> MakeBatch(long n, int size) => new()
    {
        BatchNumber = n,
        Items = Enumerable.Range(0, size).ToArray(),
        FetchedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task ProcessesEveryBatch_Sequentially_UntilExhausted()
    {
        var options = new BatchProcessingOptions { MaxParallelismPerBatch = 4, FailureRateThreshold = 1.0 };
        var executor = new ParallelBatchExecutor(options, NullLogger<ParallelBatchExecutor>.Instance);
        var coordinator = new SequentialBatchCoordinator(executor, options, NullLogger<SequentialBatchCoordinator>.Instance);

        var source = new InMemorySource
        {
            Batches = new[]
            {
                MakeBatch(1, 5),
                MakeBatch(2, 5),
                MakeBatch(3, 5),
            },
        };
        var processor = new SucceedingProcessor();

        var summary = await coordinator.RunAsync<int>(source, processor, Ctx(), CancellationToken.None);

        summary.TotalBatches.Should().Be(3);
        summary.TotalItems.Should().Be(15);
        summary.TotalSucceeded.Should().Be(15);
        summary.ExhaustedSource.Should().BeTrue();
        summary.AbortedOnThreshold.Should().BeFalse();
    }

    [Fact]
    public async Task Stops_When_FailureRateThreshold_Exceeded()
    {
        var options = new BatchProcessingOptions
        {
            MaxParallelismPerBatch = 4,
            FailureRateThreshold = 0.4,   // any batch > 40% failure aborts the run
        };
        var executor = new ParallelBatchExecutor(options, NullLogger<ParallelBatchExecutor>.Instance);
        var coordinator = new SequentialBatchCoordinator(executor, options, NullLogger<SequentialBatchCoordinator>.Instance);

        var source = new InMemorySource
        {
            Batches = new[]
            {
                MakeBatch(1, 10),   // half failing → 50% > 40% → abort
                MakeBatch(2, 10),   // should never be processed
            },
        };
        var processor = new HalfFailingProcessor();

        var summary = await coordinator.RunAsync<int>(source, processor, Ctx(), CancellationToken.None);

        summary.AbortedOnThreshold.Should().BeTrue();
        summary.TotalBatches.Should().Be(1);
        source.Emitted.Should().Be(1);
    }

    [Fact]
    public async Task EmptyBatch_TerminatesRun()
    {
        var options = new BatchProcessingOptions();
        var executor = new ParallelBatchExecutor(options, NullLogger<ParallelBatchExecutor>.Instance);
        var coordinator = new SequentialBatchCoordinator(executor, options, NullLogger<SequentialBatchCoordinator>.Instance);

        var source = new InMemorySource
        {
            Batches = new[]
            {
                MakeBatch(1, 3),
                new Batch<int>
                {
                    BatchNumber = 2,
                    Items = Array.Empty<int>(),
                    FetchedAtUtc = DateTimeOffset.UtcNow,
                },
            },
        };
        var summary = await coordinator.RunAsync<int>(source, new SucceedingProcessor(), Ctx(), CancellationToken.None);

        summary.ExhaustedSource.Should().BeTrue();
        summary.TotalBatches.Should().Be(1);
    }
}
