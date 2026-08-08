using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Abstractions.Monitoring;
using MFilesExporter.Application.Abstractions.Retry;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Infrastructure.HealthChecks;
using MFilesExporter.Infrastructure.Monitoring;
using MFilesExporter.Infrastructure.Retry;
using MFilesExporter.Infrastructure.Telemetry;
using MFilesExporter.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;

namespace MFilesExporter.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddExporterInfrastructure(this IServiceCollection services, TelemetryOptions telemetry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(telemetry);

        services.AddSingleton<IClock, SystemClock>();

        services.AddSingleton<IFailureClassifier, ExceptionClassifier>();
        services.AddSingleton<IRetryObserver, LoggingRetryObserver>();
        services.AddSingleton<IRetryObserver, MetricsRetryObserver>();
        services.AddSingleton<IRetryExecutor, RetryExecutor>();
        services.AddSingleton(TimeProvider.System);

        // Monitoring — central metric adapter + observable gauges.
        services.AddSingleton<ExporterMetrics>();
        services.AddSingleton<IExporterMetrics>(sp => sp.GetRequiredService<ExporterMetrics>());
        services.AddSingleton<ObservableGaugeRegistry>();
        services.AddHostedService<MonitoringActivator>();

        services.AddHealthChecks()
            .AddCheck<MFilesSqlHealthCheck>("mfiles-sql", tags: new[] { "ready" })
            .AddCheck<StateStoreHealthCheck>("state-store", tags: new[] { "ready" })
            .AddCheck<StorageHealthCheck>("storage", tags: new[] { "ready" });

        services.AddExporterOpenTelemetry(telemetry);

        return services;
    }
}
