using FluentValidation;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MFilesExporter.Configuration.DependencyInjection;

public static class ConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ExporterOptions tree, its validators, and exposes each
    /// sub-options object as a singleton for direct injection.
    /// </summary>
    public static IServiceCollection AddExporterConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ExporterOptions>()
            .Bind(configuration.GetSection(ExporterOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidator<ExporterOptions>, ExporterOptionsValidator>();
        services.AddSingleton<IValidateOptions<ExporterOptions>>(sp =>
            new FluentValidateOptions<ExporterOptions>(null,
                sp.GetRequiredService<IValidator<ExporterOptions>>()));

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Source);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Storage);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Pipeline);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.StateStore);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.TrackingDatabase);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.BatchProcessing);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.SqlStreaming);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.FileExport);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Metadata);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Validation);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Checkpoint);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.RetryHandling);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Telemetry);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Dashboard);

        return services;
    }
}
