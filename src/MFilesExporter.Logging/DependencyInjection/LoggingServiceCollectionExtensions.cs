using MFilesExporter.Logging.Audit;
using MFilesExporter.Logging.Correlation;
using MFilesExporter.Logging.Performance;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;

namespace MFilesExporter.Logging.DependencyInjection;

public static class LoggingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the enterprise logging primitives (correlation, audit,
    /// performance). Callers still need to configure Serilog itself via
    /// <see cref="SerilogBootstrap"/> and <c>AddSerilog(...)</c> from the host.
    /// </summary>
    public static IServiceCollection AddExporterLogging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ICorrelationIdAccessor, CorrelationIdAccessor>();
        services.AddSingleton<ILogEventEnricher, CorrelationIdEnricher>();
        services.AddSingleton<IAuditLog, AuditLog>();
        services.AddSingleton<IPerformanceLogger, PerformanceLogger>();

        return services;
    }
}
