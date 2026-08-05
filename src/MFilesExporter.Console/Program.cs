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
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = SerilogBootstrap.CreateBootstrapLogger();

// --status short-circuit — read-only report, no host build required.
if (StatusCommand.IsRequested(args))
{
    return await StatusCommand.RunAsync(args, CancellationToken.None).ConfigureAwait(false);
}

try
{
    // WindowsServiceHelpers.IsWindowsService() returns true iff started by the
    // Service Control Manager. Under `sc start` we auto-configure the host with
    // Windows Service integration; under `dotnet run` we stay a plain console.
    var isWindowsService = WindowsServiceHelpers.IsWindowsService();

    var options = new HostApplicationBuilderSettings
    {
        Args = args,
        // Services start with %WINDIR%\System32 as their cwd; force the content
        // root back to the executable directory so relative paths in
        // appsettings resolve the way developers expect.
        ContentRootPath = isWindowsService ? AppContext.BaseDirectory : null,
    };

    var builder = Host.CreateApplicationBuilder(options);

    builder.Configuration
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables(prefix: "MFILESEXPORTER_")
        .AddCommandLine(args);

    if (isWindowsService)
    {
        builder.Services.AddWindowsService(o =>
        {
            o.ServiceName = "MFilesExporter";
        });
    }

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

    Log.Information(
        "MFilesExporter starting in {Environment} (mode={Mode})",
        host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName,
        isWindowsService ? "WindowsService" : "Console");

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
