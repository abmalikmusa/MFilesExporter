using MFilesExporter.Application.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MFilesExporter.Infrastructure.HealthChecks;

public sealed class StateStoreHealthCheck : IHealthCheck
{
    private readonly IExportStateStore _store;

    public StateStoreHealthCheck(IExportStateStore store) => _store = store;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var counters = await _store.GetCountersAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy($"State store OK. Recorded={counters.TotalRecorded}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("State store not reachable.", ex);
        }
    }
}
