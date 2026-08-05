using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Parallel;

/// <summary>
/// Hosts a <see cref="IParallelProcessingEngine{TItem}"/> under the .NET
/// Generic Host so that <c>StartAsync</c> / <c>StopAsync</c> are wired
/// into the host lifecycle. Producers still push items via
/// <see cref="IParallelProcessingEngine{TItem}.Writer"/> — this class does
/// NOT drive the producer.
/// </summary>
public sealed class ParallelProcessingHostedService<TItem> : IHostedService
{
    private readonly IParallelProcessingEngine<TItem> _engine;
    private readonly ILogger<ParallelProcessingHostedService<TItem>> _logger;

    public ParallelProcessingHostedService(
        IParallelProcessingEngine<TItem> engine,
        ILogger<ParallelProcessingHostedService<TItem>> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting parallel processing engine hosted service for {ItemType}", typeof(TItem).Name);
        await _engine.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping parallel processing engine hosted service for {ItemType}", typeof(TItem).Name);
        await _engine.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
