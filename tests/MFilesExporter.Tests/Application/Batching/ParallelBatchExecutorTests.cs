using FluentAssertions;
using MFilesExporter.Application.Batching;
using MFilesExporter.Application.Common;
using MFilesExporter.Configuration.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace MFilesExporter.Tests.Application.Batching;

public class ParallelBatchExecutorTests
{
    private static BatchContext Ctx() => new()
    {
        ExportJobId = 1,
        WorkerId = 2,
        PartitionKey = "p",
        CorrelationId = CorrelationId.New(),
    };

    private static Batch<int> BatchOf(int size, long batchNumber = 1)
    {
        var items = Enumerable.Range(0, size).ToArray();
        return new Batch<int>
        {
            BatchNumber = batchNumber,
            Items = items,
            FetchedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private sealed class ConfigurableProcessor : IBatchItemProcessor<int>
    {
        public Func<int, BatchItemResult> Result { get; init; } = _ => BatchItemResult.Succeeded(0);
        public int MaxObservedConcurrency;
        private int _current;

        public async Task<BatchItemResult> ProcessAsync(int item, BatchContext ctx, CancellationToken ct)
        {
            var here = Interlocked.Increment(ref _current);
            var seen = MaxObservedConcurrency;
            while (here > seen && Interlocked.CompareExchange(ref MaxObservedConcurrency, here, seen) != seen)
            {
                seen = MaxObservedConcurrency;
            }
            try
            {
                await Task.Yield();
                await Task.Delay(5, ct).ConfigureAwait(false);
                return Result(item);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    [Fact]
    public async Task Aggregates_Succeeded_Failed_Skipped_Correctly()
    {
        var options = new BatchProcessingOptions { MaxParallelismPerBatch = 4, BatchTimeout = TimeSpan.FromMinutes(1) };
        var executor = new ParallelBatchExecutor(options, NullLogger<ParallelBatchExecutor>.Instance);
        var processor = new ConfigurableProcessor
        {
            Result = i => (i % 3) switch
            {
                0 => BatchItemResult.Succeeded(100),
                1 => BatchItemResult.Failed("boom"),
                _ => BatchItemResult.Skipped("skip"),
            },
        };

        var result = await executor.ExecuteAsync(BatchOf(30), processor, Ctx(), CancellationToken.None);

        result.Size.Should().Be(30);
        result.SucceededCount.Should().Be(10);
        result.FailedCount.Should().Be(10);
        result.SkippedCount.Should().Be(10);
        result.TotalBytesWritten.Should().Be(10 * 100);
        result.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task RespectsMaxParallelism()
    {
        var options = new BatchProcessingOptions { MaxParallelismPerBatch = 4, BatchTimeout = TimeSpan.FromMinutes(1) };
        var executor = new ParallelBatchExecutor(options, NullLogger<ParallelBatchExecutor>.Instance);
        var processor = new ConfigurableProcessor();

        _ = await executor.ExecuteAsync(BatchOf(50), processor, Ctx(), CancellationToken.None);

        processor.MaxObservedConcurrency.Should().BeGreaterThan(1);
        processor.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task ProcessorException_ConvertsToFailed_NotBatchFault()
    {
        var options = new BatchProcessingOptions { MaxParallelismPerBatch = 2, BatchTimeout = TimeSpan.FromMinutes(1) };
        var executor = new ParallelBatchExecutor(options, NullLogger<ParallelBatchExecutor>.Instance);
        var processor = new ThrowingProcessor();

        var result = await executor.ExecuteAsync(BatchOf(4), processor, Ctx(), CancellationToken.None);

        result.FailedCount.Should().Be(4);
        result.SucceededCount.Should().Be(0);
    }

    [Fact]
    public async Task EmptyBatch_ReturnsEmptyResult_WithoutInvokingProcessor()
    {
        var options = new BatchProcessingOptions();
        var executor = new ParallelBatchExecutor(options, NullLogger<ParallelBatchExecutor>.Instance);
        var processor = new ConfigurableProcessor();

        var batch = new Batch<int>
        {
            BatchNumber = 1,
            Items = Array.Empty<int>(),
            FetchedAtUtc = DateTimeOffset.UtcNow,
        };

        var result = await executor.ExecuteAsync(batch, processor, Ctx(), CancellationToken.None);

        result.Size.Should().Be(0);
        result.SucceededCount.Should().Be(0);
    }

    private sealed class ThrowingProcessor : IBatchItemProcessor<int>
    {
        public Task<BatchItemResult> ProcessAsync(int item, BatchContext ctx, CancellationToken ct) =>
            throw new InvalidOperationException("simulated");
    }
}
