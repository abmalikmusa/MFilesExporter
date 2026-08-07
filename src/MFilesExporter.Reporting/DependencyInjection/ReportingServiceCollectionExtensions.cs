using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Abstractions.Dashboard;
using MFilesExporter.Application.Abstractions.Retry;
using MFilesExporter.Reporting.Dashboard;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace MFilesExporter.Reporting.DependencyInjection;

public static class ReportingServiceCollectionExtensions
{
    public static IServiceCollection AddExporterReporting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IProgressReporter, LoggingProgressReporter>();
        services.AddHostedService<ProgressPublisherHostedService>();

        // Dashboard — activity feed, resource sampler, aggregating state source,
        // Spectre renderer, and the hosted service that owns the Live loop.
        services.AddSingleton<IWorkerActivityFeed, WorkerActivityFeed>();
        services.AddSingleton<SystemResourceSampler>();
        services.AddSingleton<IDashboardStateSource, DashboardStateSource>();
        services.AddSingleton<DashboardRenderer>();

        // Missing dashboard providers — now backed by real sources.
        services.AddSingleton<ITotalExpectedSource, TotalExpectedFromJobRepository>();
        services.AddSingleton<RetryCounterObserver>();
        services.AddSingleton<IRetryCounterSource>(sp => sp.GetRequiredService<RetryCounterObserver>());
        services.AddSingleton<IRetryObserver>(sp => sp.GetRequiredService<RetryCounterObserver>());
        services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);
        services.AddHostedService<ConsoleDashboardHostedService>();

        return services;
    }
}
