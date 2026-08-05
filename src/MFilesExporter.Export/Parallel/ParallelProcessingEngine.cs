using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Parallel;

/// <summary>
/// Default generic <see cref="IParallelProcessingEngine{TItem}"/> — a
/// producer/consumer engine built on <see cref="Channel{TItem}"/> with a
/// fixed-size worker pool, an async pause gate, per-worker heartbeats,
/// and graceful shutdown.
///
/// Threading model: exactly <c>WorkerCount</c> worker tasks are spawned in
/// <see cref="StartAsync"/>; each runs <see cref="RunWorkerAsync"/> until
/// the channel is completed and drained. All shared state (counters,
/// pause gate, heartbeat channel) is lock-free — updates go through
/// <see cref="Interlocked"/> or a channel writer.
/// </summary>
public sealed class ParallelProcessingEngine<TItem> : IParallelProcessingEngine<TItem>
{
    private readonly IParallelWorker<TItem> _worker;
    private readonly ParallelProcessingOptions _options;
    private readonly IClock _clock;
    private readonly WorkerHealthMonitor _health;
    private readonly ILogger<ParallelProcessingEngine<TItem>> _logger;

    private readonly Channel<TItem> _work;
    private readonly Channel<WorkerHeartbeatEvent> _heartbeats;
    private readonly AsyncManualResetEvent _pauseGate;
    private readonly CancellationTokenSource _internalCts;
    private readonly string _poolName;

    private readonly List<Task> _workerTasks = new();
    private int _state = (int)EngineState.NotStarted;

    private long _totalProcessed;
    private long _totalFailed;

    public ParallelProcessingEngine(
        IParallelWorker<TItem> worker,
        ParallelProcessingOptions options,
        IClock clock,
        WorkerHealthMonitor health,
        ILogger<ParallelProcessingEngine<TItem>> logger)
    {
        _worker = worker;
        _options = options;
        _clock = clock;
        _health = health;
        _logger = logger;
        _poolName = $"ppe-{typeof(TItem).Name}";
        _pauseGate = new AsyncManualResetEvent(initiallySet: true);
        _internalCts = new CancellationTokenSource();

        _work = Channel.CreateBounded<TItem>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            FullMode = Map(_options.FullMode),
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        // Heartbeats channel is unbounded — heartbeats are tiny and dropping
        // one would defeat the point of the health-monitor primary use case.
        _heartbeats = Channel.CreateUnbounded<WorkerHeartbeatEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public EngineState State => (EngineState)Volatile.Read(ref _state);
    public ChannelWriter<TItem> Writer => _work.Writer;
    public IAsyncEnumerable<WorkerHeartbeatEvent> Heartbeats => ReadHeartbeatsAsync();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!TryTransition(EngineState.NotStarted, EngineState.Running))
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting {PoolName} with {Workers} workers, channel capacity {Capacity}",
            _poolName, _options.WorkerCount, _options.ChannelCapacity);

        for (var i = 0; i < _options.WorkerCount; i++)
        {
            var id = i;
            _health.RegisterWorker(id, _clock.UtcNow);
            _workerTasks.Add(Task.Run(() => RunWorkerAsync(id, _internalCts.Token), CancellationToken.None));
        }
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        if (TryTransition(EngineState.Running, EngineState.Paused))
        {
            _pauseGate.Reset();
            _logger.LogInformation("{PoolName} paused", _poolName);
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        if (TryTransition(EngineState.Paused, EngineState.Running))
        {
            _pauseGate.Set();
            _logger.LogInformation("{PoolName} resumed", _poolName);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var previous = (EngineState)Volatile.Read(ref _state);
        if (previous is EngineState.Stopped or EngineState.NotStarted) return;

        _state = (int)EngineState.ShuttingDown;
        _logger.LogInformation("{PoolName} shutdown initiated", _poolName);

        // Release paused workers so they observe cancellation / channel completion.
        _pauseGate.Set();

        // Signal end-of-work — workers exit their loop after draining what's in the channel.
        _work.Writer.TryComplete();

        // Wait for drain, or force-cancel after the graceful timeout.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.GracefulShutdownTimeout);

        try
        {
            await Task.WhenAll(_workerTasks).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            _logger.LogInformation("{PoolName} drained cleanly", _poolName);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("{PoolName} graceful shutdown timed out after {Timeout} — cancelling workers",
                _poolName, _options.GracefulShutdownTimeout);
            _internalCts.Cancel();

            try { await Task.WhenAll(_workerTasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }

        _heartbeats.Writer.TryComplete();
        _state = (int)EngineState.Stopped;
    }

    public EngineStatus GetStatus()
    {
        var now = _clock.UtcNow;
        var workers = _health.Snapshot(now);
        var (processed, failed) = _health.GetTotals();

        return new EngineStatus(
            State:                State,
            WorkerCount:          _options.WorkerCount,
            ItemsInChannel:       _work.Reader.Count,
            TotalItemsProcessed:  processed,
            TotalItemsFailed:     failed,
            Workers:              workers,
            ObservedAtUtc:        now);
    }

    /* ---------------------------------------------------------------
     * Worker loop
     * --------------------------------------------------------------- */
    private async Task RunWorkerAsync(int workerId, CancellationToken cancellationToken)
    {
        var context = new WorkerContext
        {
            WorkerId     = workerId,
            PoolName     = _poolName,
            StartedAtUtc = _clock.UtcNow,
        };
        long itemsProcessed = 0;
        long itemsFailed = 0;

        using var heartbeatTimer = new PeriodicTimer(_options.HeartbeatInterval);
        var heartbeatLoop = IdleHeartbeatLoopAsync(workerId, heartbeatTimer, () => (itemsProcessed, itemsFailed), cancellationToken);

        try
        {
            var reader = _work.Reader;
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.TryRead(out var item))
                {
                    continue;
                }

                // Pause gate — cheap when open (Task.CompletedTask fast path).
                if (!_pauseGate.IsSet)
                {
                    EmitHeartbeat(workerId, WorkerHeartbeatKind.Paused, itemsProcessed, itemsFailed);
                    await _pauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await _worker.ProcessAsync(item, context, cancellationToken).ConfigureAwait(false);
                    itemsProcessed++;
                    context.ItemsProcessed = itemsProcessed;
                    Interlocked.Increment(ref _totalProcessed);
                    EmitHeartbeat(workerId, WorkerHeartbeatKind.Processed, itemsProcessed, itemsFailed);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    itemsFailed++;
                    Interlocked.Increment(ref _totalFailed);
                    _logger.LogError(ex,
                        "Worker {WorkerId} in {PoolName} — handler threw for one item",
                        workerId, _poolName);
                    EmitHeartbeat(workerId, WorkerHeartbeatKind.Failed, itemsProcessed, itemsFailed);

                    if (_options.RestartWorkersOnFault)
                    {
                        // Fault-tolerance mode: continue processing further items.
                        continue;
                    }
                    // Default: continue as well — a single item failure never
                    // shrinks the pool. Genuine cancellation is what exits.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Worker {WorkerId} in {PoolName} cancelled", workerId, _poolName);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "Worker {WorkerId} in {PoolName} exited unexpectedly",
                workerId, _poolName);
        }
        finally
        {
            heartbeatTimer.Dispose();
            try { await heartbeatLoop.ConfigureAwait(false); } catch { /* silent */ }

            EmitHeartbeat(workerId, WorkerHeartbeatKind.Stopped, itemsProcessed, itemsFailed);
            _health.MarkStopped(workerId, _clock.UtcNow);
        }
    }

    private async Task IdleHeartbeatLoopAsync(
        int workerId,
        PeriodicTimer timer,
        Func<(long processed, long failed)> countersProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var (p, f) = countersProvider();
                EmitHeartbeat(workerId, WorkerHeartbeatKind.Idle, p, f);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
    }

    private void EmitHeartbeat(int workerId, WorkerHeartbeatKind kind, long processed, long failed)
    {
        var evt = new WorkerHeartbeatEvent(
            WorkerId:            workerId,
            PoolName:            _poolName,
            Kind:                kind,
            EmittedAtUtc:        _clock.UtcNow,
            ItemsProcessedTotal: processed,
            ItemsFailedTotal:    failed);

        // Unbounded — always accepts. Also fire the monitor synchronously
        // so `GetStatus()` reads fresh data without waiting on the consumer.
        _heartbeats.Writer.TryWrite(evt);
        _health.RecordHeartbeat(evt);
    }

    private async IAsyncEnumerable<WorkerHeartbeatEvent> ReadHeartbeatsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reader = _heartbeats.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var evt))
            {
                yield return evt;
            }
        }
    }

    private bool TryTransition(EngineState expected, EngineState next) =>
        Interlocked.CompareExchange(ref _state, (int)next, (int)expected) == (int)expected;

    private static BoundedChannelFullMode Map(ChannelFullMode mode) => mode switch
    {
        ChannelFullMode.Wait       => BoundedChannelFullMode.Wait,
        ChannelFullMode.DropOldest => BoundedChannelFullMode.DropOldest,
        ChannelFullMode.DropNewest => BoundedChannelFullMode.DropNewest,
        _ => BoundedChannelFullMode.Wait,
    };
}
