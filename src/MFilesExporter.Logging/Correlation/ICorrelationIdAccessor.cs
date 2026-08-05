namespace MFilesExporter.Logging.Correlation;

/// <summary>
/// Ambient accessor for the current correlation identifier. Backed by an
/// <see cref="AsyncLocal{T}"/> so the value flows through <c>await</c>
/// continuations and <c>Task.Run</c>/<c>Parallel.ForEach</c> children.
/// </summary>
/// <remarks>
/// <para>
/// Enterprise runs generate one correlation id per top-level export job,
/// per RPC entry point, or per worker iteration. Downstream code should
/// never invent its own id — always call <see cref="Push"/> at the boundary
/// and let inner code inherit.
/// </para>
/// <para>
/// The accessor is thread-safe. <see cref="Push"/> returns an
/// <see cref="IDisposable"/> that restores the previous value when disposed.
/// </para>
/// </remarks>
public interface ICorrelationIdAccessor
{
    /// <summary>Current correlation id, or <c>null</c> when no scope is active.</summary>
    string? Current { get; }

    /// <summary>Generate a new W3C-shaped 128-bit correlation id (32 hex chars).</summary>
    string NewId();

    /// <summary>
    /// Pushes a new correlation id onto the async-local stack, and adds a
    /// matching property to the Serilog <c>LogContext</c> so every log line
    /// carries <c>CorrelationId</c> automatically. Dispose to pop.
    /// </summary>
    IDisposable Push(string correlationId);

    /// <summary>Same as <see cref="Push"/> but auto-generates the id via <see cref="NewId"/>.</summary>
    IDisposable PushNew(out string correlationId);
}
