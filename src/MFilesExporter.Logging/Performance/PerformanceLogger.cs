using Microsoft.Extensions.Logging;

namespace MFilesExporter.Logging.Performance;

/// <summary>
/// Default <see cref="IPerformanceLogger"/>. Emits one INFO/ERROR line per
/// measured operation via a Serilog logger sub-scoped to
/// <c>Category=Performance</c> so file-sink filters route it to
/// <c>logs/performance-*.log</c>.
/// </summary>
public sealed class PerformanceLogger : IPerformanceLogger
{
    private readonly ILoggerFactory _factory;
    private readonly ILogger _logger;

    public PerformanceLogger(ILoggerFactory factory)
    {
        _factory = factory;
        _logger  = factory.CreateLogger("MFilesExporter.Performance");
    }

    public PerformanceScope Begin(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return new PerformanceScope(_logger, operation);
    }

    public async ValueTask<T> TimeAsync<T>(
        string operation,
        Func<CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken)
    {
        using var scope = Begin(operation);
        try
        {
            var result = await work(cancellationToken).ConfigureAwait(false);
            scope.Complete();
            return result;
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }
    }

    public async ValueTask TimeAsync(
        string operation,
        Func<CancellationToken, ValueTask> work,
        CancellationToken cancellationToken)
    {
        using var scope = Begin(operation);
        try
        {
            await work(cancellationToken).ConfigureAwait(false);
            scope.Complete();
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }
    }
}
