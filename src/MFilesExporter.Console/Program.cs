using MFilesExporter.Application.DependencyInjection;
using MFilesExporter.Application.UseCases;
using MFilesExporter.Configuration.DependencyInjection;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Console;
using MFilesExporter.Export.DependencyInjection;
using MFilesExporter.Infrastructure.DependencyInjection;
using MFilesExporter.Logging;
using MFilesExporter.Logging.DependencyInjection;
using MFilesExporter.Persistence.DependencyInjection;
using MFilesExporter.Reporting.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = SerilogBootstrap.CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Configuration
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables(prefix: "MFILESEXPORTER_")
        .AddCommandLine(args);

    // Configuration must load and validate before anything else touches it.
    builder.Services.AddExporterConfiguration(builder.Configuration);

    // Logging — correlation, audit, performance, worker scopes.
    builder.Services.AddExporterLogging();
    builder.Services.AddSerilog((sp, cfg) => cfg
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(sp)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithEnvironmentUserName()
        .Enrich.WithThreadId()
        .Enrich.WithProcessId()
        .Enrich.WithProperty("Application", "MFilesExporter"));

    // Infrastructure requires telemetry options at wire time; snapshot them
    // once from configuration so OTel resource attributes are stable.
    var telemetry = builder.Configuration
        .GetSection(ExporterOptions.SectionName)
        .Get<ExporterOptions>()
        ?.Telemetry ?? new TelemetryOptions();

    builder.Services.AddExporterInfrastructure(telemetry);
    builder.Services.AddExporterPersistence();
    builder.Services.AddExporterExport();
    builder.Services.AddExporterReporting();
    builder.Services.AddExporterApplication();

    builder.Services.AddHostedService<ExportHostedService>();

    var host = builder.Build();

    // Fail fast on invalid options.
    _ = host.Services.GetRequiredService<IOptions<ExporterOptions>>().Value;

    Log.Information("MFilesExporter starting in {Environment}", host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
    await host.RunAsync().ConfigureAwait(false);
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "MFilesExporter terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
