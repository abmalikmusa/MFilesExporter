using System.Collections.Concurrent;
using MFilesExporter.Application.Abstractions.Dashboard;

namespace MFilesExporter.Reporting.Dashboard;

/// <summary>
/// In-memory <see cref="IWorkerActivityFeed"/>. Each worker owns a single
/// mutable slot updated under a per-worker lock; the dashboard reads
/// snapshots at render-time.
/// </summary>
public sealed class WorkerActivityFeed : IWorkerActivityFeed
{
    private readonly ConcurrentDictionary<int, State> _workers = new();
    private readonly TimeProvider _time;

    public WorkerActivityFeed(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public void RecordStart(int workerId, string documentKey, long bytesExpected = 0, string? batchId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKey);

        var state = _workers.GetOrAdd(workerId, id => new State(id));
        lock (state.Sync)
        {
            state.Current      = documentKey;
            state.BatchId      = batchId ?? state.BatchId;
            state.BytesExpected= bytesExpected;
            state.BytesWritten = 0;
            state.StateKind    = WorkerActivityState.Busy;
            state.LastUpdate   = _time.GetUtcNow();
        }
    }

    public void RecordFinish(int workerId, WorkerActivityOutcome outcome, long bytesWritten)
    {
        var state = _workers.GetOrAdd(workerId, id => new State(id));
        lock (state.Sync)
        {
            state.BytesWritten = bytesWritten;
            state.LastOutcome  = outcome;
            state.LastUpdate   = _time.GetUtcNow();
            state.DocumentsProcessed++;
            if (outcome == WorkerActivityOutcome.Failed) state.DocumentsFailed++;
            state.StateKind    = WorkerActivityState.Idle;
            state.Current      = null;
        }
    }

    public void RecordIdle(int workerId)
    {
        var state = _workers.GetOrAdd(workerId, id => new State(id));
        lock (state.Sync)
        {
            state.StateKind  = WorkerActivityState.Idle;
            state.Current    = null;
            state.LastUpdate = _time.GetUtcNow();
        }
    }

    public IReadOnlyList<WorkerActivityEntry> Snapshot()
    {
        var result = new List<WorkerActivityEntry>(_workers.Count);
        foreach (var state in _workers.Values)
        {
            lock (state.Sync)
            {
                result.Add(new WorkerActivityEntry
                {
                    WorkerId           = state.Id,
                    State              = state.StateKind,
                    CurrentDocumentKey = state.Current,
                    CurrentBatchId     = state.BatchId,
                    BytesExpected      = state.BytesExpected,
                    BytesWritten       = state.BytesWritten,
                    LastUpdateUtc      = state.LastUpdate,
                    DocumentsProcessed = state.DocumentsProcessed,
                    DocumentsFailed    = state.DocumentsFailed,
                    LastOutcome        = state.LastOutcome,
                });
            }
        }
        result.Sort((a, b) => a.WorkerId.CompareTo(b.WorkerId));
        return result;
    }

    private sealed class State
    {
        public State(int id) { Id = id; LastUpdate = DateTimeOffset.UtcNow; StateKind = WorkerActivityState.Idle; Sync = new(); }
        public int Id;
        public WorkerActivityState StateKind;
        public string? Current;
        public string? BatchId;
        public long BytesExpected;
        public long BytesWritten;
        public DateTimeOffset LastUpdate;
        public long DocumentsProcessed;
        public long DocumentsFailed;
        public WorkerActivityOutcome? LastOutcome;
        public readonly object Sync;
    }
}
