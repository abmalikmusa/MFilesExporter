using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Logging.Performance;

/// <summary>
/// Single-use disposable that records the elapsed time of a named operation
/// when disposed. Safe to <c>using</c> — always emits a line even if the
/// enclosing code throws.
/// </summary>
public sealed class PerformanceScope : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _operation;
    private readonly long _startTicks;
    private readonly Dictionary<string, object?> _tags = new(StringComparer.Ordinal);

    private string _outcome = "unknown";
    private Exception? _exception;
    private int _disposed;

    internal PerformanceScope(ILogger logger, string operation)
    {
        _logger     = logger;
        _operation  = operation;
        _startTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>Attach an ad-hoc property (dimension) to the resulting log line.</summary>
    public PerformanceScope SetTag(string name, object? value)
    {
        _tags[name] = value;
        return this;
    }

    /// <summary>Mark the scope as successful. Optionally provide bytes written / items processed.</summary>
    public PerformanceScope Complete(long? bytes = null, long? items = null)
    {
        _outcome = "success";
        if (bytes is not null) _tags["Bytes"] = bytes.Value;
        if (items is not null) _tags["Items"] = items.Value;
        return this;
    }

    /// <summary>Mark the scope as failed. Called automatically if an exception unwinds the scope.</summary>
    public PerformanceScope Fail(Exception exception)
    {
        _outcome    = "failed";
        _exception  = exception;
        return this;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        var elapsed = Stopwatch.GetElapsedTime(_startTicks);
        var elapsedMs = elapsed.TotalMilliseconds;

        // Prefer BeginScope so tag values flow via the structured pipeline
        // rather than being smashed into the message template.
        using (_logger.BeginScope(_tags))
        {
            if (_exception is null)
            {
                _logger.LogInformation(
                    "perf.operation op={Operation} outcome={Outcome} elapsed_ms={ElapsedMs:F2} category={Category}",
                    _operation, _outcome, elapsedMs, LogCategories.Performance);
            }
            else
            {
                _logger.LogError(_exception,
                    "perf.operation op={Operation} outcome={Outcome} elapsed_ms={ElapsedMs:F2} category={Category}",
                    _operation, _outcome, elapsedMs, LogCategories.Performance);
            }
        }
    }
}
