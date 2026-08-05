namespace MFilesExporter.Export.Parallel;

/// <summary>
/// Async-friendly manual-reset event used as the pause gate. Workers
/// <c>await WaitAsync</c> between items; <c>Reset()</c> blocks new work,
/// <c>Set()</c> releases everyone.
/// </summary>
/// <remarks>
/// Implemented over a <see cref="TaskCompletionSource"/> swap protected by
/// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>. The
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> flag
/// ensures that awakening does not run continuations on the setter's
/// thread — critical for hosted-service graceful shutdown.
/// </remarks>
public sealed class AsyncManualResetEvent
{
    private TaskCompletionSource _tcs;

    public AsyncManualResetEvent(bool initiallySet)
    {
        _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (initiallySet)
        {
            _tcs.TrySetResult();
        }
    }

    /// <summary>True when currently set (waiters proceed immediately).</summary>
    public bool IsSet => Volatile.Read(ref _tcs).Task.IsCompleted;

    /// <summary>Waits until the event is set. Returns instantly if already set.</summary>
    public Task WaitAsync(CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _tcs).Task.WaitAsync(cancellationToken);

    /// <summary>Releases every current and future waiter until <see cref="Reset"/> is called.</summary>
    public void Set()
    {
        var current = Volatile.Read(ref _tcs);
        current.TrySetResult();
    }

    /// <summary>Blocks future waiters. Waiters already released by a prior <see cref="Set"/> are unaffected.</summary>
    public void Reset()
    {
        while (true)
        {
            var current = Volatile.Read(ref _tcs);
            if (!current.Task.IsCompleted)
            {
                // Already reset (waiters are blocked).
                return;
            }
            var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _tcs, fresh, current), current))
            {
                return;
            }
        }
    }
}
