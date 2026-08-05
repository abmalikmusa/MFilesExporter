using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Export.Parallel;
using Microsoft.Extensions.Logging.Abstractions;

namespace MFilesExporter.Tests.Export.Parallel;

public class WorkerHealthMonitorTests
{
    private static WorkerHealthMonitor NewMonitor(TimeSpan stalledThreshold) =>
        new(new ParallelProcessingOptions { StalledThreshold = stalledThreshold },
            NullLogger<WorkerHealthMonitor>.Instance);

    [Fact]
    public void RegisterWorker_ProducesHealthySnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var monitor = NewMonitor(TimeSpan.FromSeconds(30));
        monitor.RegisterWorker(0, now);

        var snapshot = monitor.Snapshot(now).Single();
        snapshot.WorkerId.Should().Be(0);
        snapshot.Liveness.Should().Be(WorkerLiveness.Healthy);
    }

    [Fact]
    public void StaleHeartbeat_FlagsWorkerAsStalled()
    {
        var monitor = NewMonitor(TimeSpan.FromSeconds(10));
        var start = DateTimeOffset.UtcNow;
        monitor.RegisterWorker(0, start);
        monitor.RecordHeartbeat(new WorkerHeartbeatEvent(0, "p", WorkerHeartbeatKind.Idle, start, 0, 0));

        var later = start.AddSeconds(30);
        var snapshot = monitor.Snapshot(later).Single();
        snapshot.Liveness.Should().Be(WorkerLiveness.Stalled);
        snapshot.HeartbeatAge.Should().BeCloseTo(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void StoppedHeartbeat_FreezesLivenessLabel()
    {
        var monitor = NewMonitor(TimeSpan.FromSeconds(10));
        var now = DateTimeOffset.UtcNow;
        monitor.RegisterWorker(0, now);
        monitor.RecordHeartbeat(new WorkerHeartbeatEvent(0, "p", WorkerHeartbeatKind.Stopped, now, 5, 1));

        var snapshot = monitor.Snapshot(now.AddSeconds(60)).Single();
        snapshot.Liveness.Should().Be(WorkerLiveness.Stopped);
        snapshot.ItemsProcessed.Should().Be(5);
    }

    [Fact]
    public void Totals_SumsAcrossWorkers()
    {
        var monitor = NewMonitor(TimeSpan.FromSeconds(30));
        var now = DateTimeOffset.UtcNow;
        monitor.RecordHeartbeat(new WorkerHeartbeatEvent(0, "p", WorkerHeartbeatKind.Processed, now, 100, 2));
        monitor.RecordHeartbeat(new WorkerHeartbeatEvent(1, "p", WorkerHeartbeatKind.Processed, now, 200, 5));

        var (processed, failed) = monitor.GetTotals();
        processed.Should().Be(300);
        failed.Should().Be(7);
    }

    [Fact]
    public void MarkStopped_UpgradesFromHealthyToStopped()
    {
        var monitor = NewMonitor(TimeSpan.FromSeconds(30));
        var now = DateTimeOffset.UtcNow;
        monitor.RegisterWorker(0, now);
        monitor.MarkStopped(0, now.AddSeconds(5));

        monitor.Snapshot(now.AddSeconds(6)).Single().Liveness.Should().Be(WorkerLiveness.Stopped);
    }
}
