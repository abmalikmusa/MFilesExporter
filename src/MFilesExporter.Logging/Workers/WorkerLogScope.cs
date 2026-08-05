using Serilog.Context;

namespace MFilesExporter.Logging.Workers;

/// <summary>
/// Ambient scope that tags every log line inside its lifetime with
/// <c>WorkerId</c>, <c>WorkerName</c>, and <c>Category=Worker</c>.
/// </summary>
/// <remarks>
/// <para>
/// Use once when a worker starts iterating and dispose when it stops:
/// <code>
/// using var _ = WorkerLogScope.Enter(workerId, workerName);
/// while (!ct.IsCancellationRequested) { ... }
/// </code>
/// The scope flows through <c>await</c> continuations via
/// <see cref="LogContext"/>, so downstream loggers automatically inherit the
/// worker context — no need to pass the id through every method.
/// </para>
/// <para>
/// Nested scopes are supported; each disposal pops one entry.
/// </para>
/// </remarks>
public static class WorkerLogScope
{
    public const string WorkerIdProperty   = "WorkerId";
    public const string WorkerNameProperty = "WorkerName";

    /// <summary>Enter a worker scope. Dispose to leave.</summary>
    public static IDisposable Enter(string workerId, string? workerName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        var id       = LogContext.PushProperty(WorkerIdProperty, workerId);
        var name     = LogContext.PushProperty(WorkerNameProperty, workerName ?? workerId);
        var category = LogContext.PushProperty(LogCategories.PropertyName, LogCategories.Worker);
        return new Aggregate(id, name, category);
    }

    /// <summary>Enter a worker scope keyed on an integer id (typical for the parallel processing engine).</summary>
    public static IDisposable Enter(int workerId, string? workerName = null)
        => Enter(workerId.ToString(System.Globalization.CultureInfo.InvariantCulture), workerName);

    private sealed class Aggregate : IDisposable
    {
        private readonly IDisposable _a, _b, _c;
        private int _disposed;

        public Aggregate(IDisposable a, IDisposable b, IDisposable c)
        {
            _a = a; _b = b; _c = c;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Dispose in reverse of push order so the LogContext stack unwinds cleanly.
            _c.Dispose();
            _b.Dispose();
            _a.Dispose();
        }
    }
}
