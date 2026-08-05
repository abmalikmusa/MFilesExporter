using FluentAssertions;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Export.Parallel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MFilesExporter.Tests.Export.Parallel;

public class ParallelProcessingEngineTests
{
    private sealed class CountingWorker : IParallelWorker<int>
    {
        public int MaxObservedConcurrency;
        private int _current;
        public int ProcessedCount;
        public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(5);

        public async Task ProcessAsync(int item, WorkerContext ctx, CancellationToken ct)
        {
            var here = Interlocked.Increment(ref _current);
            var seen = MaxObservedConcurrency;
            while (here > seen && Interlocked.CompareExchange(ref MaxObservedConcurrency, here, seen) != seen)
            {
                seen = MaxObservedConcurrency;
            }
            try
            {
                await Task.Delay(Delay, ct);
                Interlocked.Increment(ref ProcessedCount);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    private sealed class ThrowingWorker : IParallelWorker<int>
    {
        public int ProcessedCount;
        public int FailedCount;
        public int FailEveryNth { get; set; } = 3;

        public Task ProcessAsync(int item, WorkerContext ctx, CancellationToken ct)
        {
            if (item % FailEveryNth == 0)
            {
                Interlocked.Increment(ref FailedCount);
                throw new InvalidOperationException("nope");
            }
            Interlocked.Increment(ref ProcessedCount);
            return Task.CompletedTask;
        }
    }

    private static IClock RealClock()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(_ => DateTimeOffset.UtcNow);
        return clock;
    }

    private static ParallelProcessingEngine<int> Build(
        IParallelWorker<int> worker,
        int workers = 4,
        int capacity = 16,
        TimeSpan? heartbeat = null,
        TimeSpan? shutdownTimeout = null)
    {
        var opts = new ParallelProcessingOptions
        {
            WorkerCount = workers,
            ChannelCapacity = capacity,
            HeartbeatInterval = heartbeat ?? TimeSpan.FromMilliseconds(50),
            StalledThreshold = TimeSpan.FromSeconds(1),
            GracefulShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5),
        };
        var clock = RealClock();
        var health = new WorkerHealthMonitor(opts, NullLogger<WorkerHealthMonitor>.Instance);
        return new ParallelProcessingEngine<int>(worker, opts, clock, health,
            NullLogger<ParallelProcessingEngine<int>>.Instance);
    }

    [Fact]
    public async Task ProcessesEveryItem_UntilShutdown()
    {
        var worker = new CountingWorker { Delay = TimeSpan.FromMilliseconds(1) };
        var engine = Build(worker, workers: 4);
        await engine.StartAsync(default);

        for (var i = 0; i < 100; i++)
        {
            await engine.Writer.WriteAsync(i);
        }
        await engine.StopAsync(default);

        worker.ProcessedCount.Should().Be(100);
        engine.State.Should().Be(EngineState.Stopped);
    }

    [Fact]
    public async Task ExhibitsConfiguredParallelism()
    {
        var worker = new CountingWorker { Delay = TimeSpan.FromMilliseconds(20) };
        var engine = Build(worker, workers: 4);
        await engine.StartAsync(default);

        for (var i = 0; i < 50; i++)
        {
            await engine.Writer.WriteAsync(i);
        }
        await engine.StopAsync(default);

        worker.MaxObservedConcurrency.Should().BeGreaterThan(1);
        worker.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task PauseAndResume_HaltsAndResumesProcessing()
    {
        var worker = new CountingWorker { Delay = TimeSpan.FromMilliseconds(1) };
        var engine = Build(worker, workers: 2);
        await engine.StartAsync(default);

        // Enqueue a batch, then pause.
        for (var i = 0; i < 10; i++) await engine.Writer.WriteAsync(i);
        await Task.Delay(100);
        await engine.PauseAsync(default);
        engine.State.Should().Be(EngineState.Paused);

        var midpoint = worker.ProcessedCount;

        // While paused, further writes should not be processed.
        for (var i = 10; i < 20; i++) await engine.Writer.WriteAsync(i);
        await Task.Delay(150);
        worker.ProcessedCount.Should().Be(midpoint,
            "workers should not consume new items while paused");

        await engine.ResumeAsync(default);
        engine.State.Should().Be(EngineState.Running);

        await engine.StopAsync(default);
        worker.ProcessedCount.Should().Be(20);
    }

    [Fact]
    public async Task GracefulShutdown_DrainsInFlight()
    {
        var worker = new CountingWorker { Delay = TimeSpan.FromMilliseconds(20) };
        var engine = Build(worker, workers: 2, shutdownTimeout: TimeSpan.FromSeconds(5));
        await engine.StartAsync(default);

        for (var i = 0; i < 30; i++) await engine.Writer.WriteAsync(i);
        await engine.StopAsync(default);

        worker.ProcessedCount.Should().Be(30, "graceful shutdown drains the queue");
    }

    [Fact]
    public async Task HandlerExceptions_DoNotStopTheEngine()
    {
        var worker = new ThrowingWorker { FailEveryNth = 3 };
        var engine = Build(worker, workers: 2);
        await engine.StartAsync(default);

        for (var i = 1; i <= 30; i++) await engine.Writer.WriteAsync(i);
        await engine.StopAsync(default);

        worker.ProcessedCount.Should().Be(20);   // 30 - 10 failures on multiples of 3
        worker.FailedCount.Should().Be(10);
        engine.State.Should().Be(EngineState.Stopped);
    }

    [Fact]
    public async Task Status_ReflectsProgress_AndWorkerCount()
    {
        var worker = new CountingWorker();
        var engine = Build(worker, workers: 3);
        await engine.StartAsync(default);
        for (var i = 0; i < 10; i++) await engine.Writer.WriteAsync(i);
        await engine.StopAsync(default);

        var status = engine.GetStatus();
        status.WorkerCount.Should().Be(3);
        status.TotalItemsProcessed.Should().Be(10);
        status.Workers.Should().HaveCount(3);
        status.State.Should().Be(EngineState.Stopped);
    }

    [Fact]
    public async Task Heartbeats_AsyncStream_EmitsEvents()
    {
        var worker = new CountingWorker { Delay = TimeSpan.FromMilliseconds(5) };
        var engine = Build(worker, workers: 2, heartbeat: TimeSpan.FromMilliseconds(20));
        await engine.StartAsync(default);

        for (var i = 0; i < 5; i++) await engine.Writer.WriteAsync(i);

        var collected = new List<WorkerHeartbeatEvent>();
        var collectorCts = new CancellationTokenSource();
        var collector = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in engine.Heartbeats.WithCancellation(collectorCts.Token))
                {
                    collected.Add(evt);
                    if (collected.Count >= 5) break;
                }
            }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(200);
        collectorCts.Cancel();
        await engine.StopAsync(default);
        await collector;

        collected.Should().NotBeEmpty();
        collected.Select(h => h.Kind).Should().Contain(new[] { WorkerHeartbeatKind.Processed });
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        var worker = new CountingWorker();
        var engine = Build(worker, workers: 2);
        await engine.StartAsync(default);
        await engine.StartAsync(default);         // no-op
        engine.State.Should().Be(EngineState.Running);
        await engine.StopAsync(default);
    }
}
