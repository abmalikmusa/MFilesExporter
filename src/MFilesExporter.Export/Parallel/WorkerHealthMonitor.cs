using System.Collections.Concurrent;
using MFilesExporter.Configuration.Options;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Parallel;

/// <summary>
/// Tracks the last heartbeat and cumulative counters per worker.
/// Consumed by the engine's <see cref="IParallelProcessingEngine{TItem}.GetStatus"/>
/// method to build per-worker diagnostics.
/// </summary>
public sealed class WorkerHealthMonitor
{
    private readonly ConcurrentDictionary<int, State> _byWorker = new();
    private readonly ParallelProcessingOptions _options;
    private readonly ILogger<WorkerHealthMonitor> _logger;

    public WorkerHealthMonitor(ParallelProcessingOptions options, ILogger<WorkerHealthMonitor> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Register a worker on start-up so its snapshot appears even before its first heartbeat.</summary>
    public void RegisterWorker(int workerId, DateTimeOffset now)
    {
        _byWorker[workerId] = new State
        {
            WorkerId = workerId,
            LastHeartbeatUtc = now,
            LastKind = WorkerHeartbeatKind.Idle,
            ItemsProcessed = 0,
            ItemsFailed = 0,
            Stopped = false,
        };
    }

    /// <summary>Record a heartbeat received from a worker.</summary>
    public void RecordHeartbeat(WorkerHeartbeatEvent evt)
    {
        _byWorker.AddOrUpdate(evt.WorkerId,
            _ => new State
            {
                WorkerId = evt.WorkerId,
                LastHeartbeatUtc = evt.EmittedAtUtc,
                LastKind = evt.Kind,
                ItemsProcessed = evt.ItemsProcessedTotal,
                ItemsFailed = evt.ItemsFailedTotal,
                Stopped = evt.Kind == WorkerHeartbeatKind.Stopped,
            },
            (_, existing) => existing with
            {
                LastHeartbeatUtc = evt.EmittedAtUtc,
                LastKind = evt.Kind,
                ItemsProcessed = evt.ItemsProcessedTotal,
                ItemsFailed = evt.ItemsFailedTotal,
                Stopped = existing.Stopped || evt.Kind == WorkerHeartbeatKind.Stopped,
            });
    }

    /// <summary>Build a snapshot for every registered worker.</summary>
    public IReadOnlyList<WorkerStatusSnapshot> Snapshot(DateTimeOffset now)
    {
        var stalled = _options.StalledThreshold;
        var snapshots = new List<WorkerStatusSnapshot>(_byWorker.Count);

        foreach (var state in _byWorker.Values.OrderBy(s => s.WorkerId))
        {
            var age = now - state.LastHeartbeatUtc;
            var liveness =
                state.Stopped ? WorkerLiveness.Stopped :
                age > stalled ? WorkerLiveness.Stalled :
                                WorkerLiveness.Healthy;

            snapshots.Add(new WorkerStatusSnapshot(
                WorkerId:          state.WorkerId,
                Liveness:          liveness,
                ItemsProcessed:    state.ItemsProcessed,
                LastHeartbeatUtc:  state.LastHeartbeatUtc,
                HeartbeatAge:      age));
        }
        return snapshots;
    }

    /// <summary>Aggregate cumulative counters across every worker.</summary>
    public (long processed, long failed) GetTotals()
    {
        long p = 0, f = 0;
        foreach (var s in _byWorker.Values)
        {
            p += s.ItemsProcessed;
            f += s.ItemsFailed;
        }
        return (p, f);
    }

    /// <summary>Marks the worker as stopped so its liveness label freezes.</summary>
    public void MarkStopped(int workerId, DateTimeOffset now)
    {
        _byWorker.AddOrUpdate(workerId,
            _ => new State { WorkerId = workerId, LastHeartbeatUtc = now, Stopped = true, LastKind = WorkerHeartbeatKind.Stopped },
            (_, s) => s with { Stopped = true, LastHeartbeatUtc = now, LastKind = WorkerHeartbeatKind.Stopped });
    }

    private sealed record State
    {
        public required int WorkerId { get; init; }
        public required DateTimeOffset LastHeartbeatUtc { get; init; }
        public required WorkerHeartbeatKind LastKind { get; init; }
        public long ItemsProcessed { get; init; }
        public long ItemsFailed { get; init; }
        public bool Stopped { get; init; }
    }
}
