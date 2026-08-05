using MFilesExporter.Configuration.Options;
using MFilesExporter.Shared.IO;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MFilesExporter.Infrastructure.HealthChecks;

public sealed class StorageHealthCheck : IHealthCheck
{
    private readonly StorageOptions _options;

    public StorageHealthCheck(StorageOptions options) => _options = options;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(_options.RootPath);
            Directory.CreateDirectory(_options.ManifestPath);

            var freeGb = FileSystemHelpers.GetAvailableFreeSpaceGb(_options.RootPath);
            if (_options.MinimumFreeSpaceGb > 0 && freeGb < _options.MinimumFreeSpaceGb)
            {
                return Task.FromResult(HealthCheckResult.Degraded(
                    $"Only {freeGb} GiB free at {_options.RootPath} (minimum {_options.MinimumFreeSpaceGb})."));
            }
            return Task.FromResult(HealthCheckResult.Healthy($"Storage OK. Free={freeGb} GiB"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Storage not writable.", ex));
        }
    }
}
