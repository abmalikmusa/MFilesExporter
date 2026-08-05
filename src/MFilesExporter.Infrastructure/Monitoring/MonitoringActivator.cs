using Microsoft.Extensions.Hosting;

namespace MFilesExporter.Infrastructure.Monitoring;

/// <summary>
/// Eagerly resolves the <see cref="ObservableGaugeRegistry"/> at host start so
/// its instruments are registered against the meter before the first
/// OpenTelemetry export tick. Without this, the DI container defers
/// construction and the first scrape misses the observable gauges.
/// </summary>
public sealed class MonitoringActivator : IHostedService
{
    private readonly ObservableGaugeRegistry _registry;

    public MonitoringActivator(ObservableGaugeRegistry registry) => _registry = registry;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Constructor of ObservableGaugeRegistry registers the gauges — nothing more to do.
        _ = _registry;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
