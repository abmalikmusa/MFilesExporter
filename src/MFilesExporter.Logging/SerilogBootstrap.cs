using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace MFilesExporter.Logging;

/// <summary>
/// Central entry point for logging configuration. Owns the bootstrap logger
/// (used before the host is built) and the host-integrated Serilog setup.
/// </summary>
/// <remarks>
/// Sink routing is driven by <c>appsettings.json</c> under <c>Serilog:WriteTo</c>.
/// See <c>docs/logging.md</c> for the four-sink layout: everything, errors-only,
/// audit-only (Category=Audit), and performance-only (Category=Performance).
/// </remarks>
public static class SerilogBootstrap
{
    /// <summary>
    /// Creates the bootstrap logger — used for anything that runs before the
    /// host is built. Reads only environment/console configuration so it can
    /// never fail on missing appsettings.
    /// </summary>
    public static ILogger CreateBootstrapLogger()
    {
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Category", LogCategories.Application)
            .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateBootstrapLogger();
    }

    /// <summary>
    /// Wires Serilog into the host so it becomes the ILogger implementation.
    /// The configuration is read from the host's IConfiguration under the
    /// "Serilog" section, allowing environment-specific sinks and levels.
    /// </summary>
    public static IHostBuilder UseExporterSerilog(this IHostBuilder builder)
    {
        return builder.UseSerilog((ctx, sp, cfg) => cfg
            .ReadFrom.Configuration(ctx.Configuration)
            .ReadFrom.Services(sp)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentUserName()
            .Enrich.WithThreadId()
            .Enrich.WithProcessId()
            .Enrich.WithProperty("Application", "MFilesExporter"));
    }
}
