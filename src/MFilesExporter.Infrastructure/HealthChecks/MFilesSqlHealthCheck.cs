using MFilesExporter.Configuration.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MFilesExporter.Infrastructure.HealthChecks;

public sealed class MFilesSqlHealthCheck : IHealthCheck
{
    private readonly MFilesSourceOptions _options;

    public MFilesSqlHealthCheck(MFilesSourceOptions options) => _options = options;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand("SELECT 1;", connection) { CommandTimeout = 5 };
            _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("SQL Server reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQL Server unreachable.", ex);
        }
    }
}
