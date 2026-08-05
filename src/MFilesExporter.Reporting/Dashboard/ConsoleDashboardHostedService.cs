using MFilesExporter.Application.Abstractions.Dashboard;
using MFilesExporter.Configuration.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace MFilesExporter.Reporting.Dashboard;

/// <summary>
/// Long-running <see cref="BackgroundService"/> that owns the
/// <see cref="AnsiConsole.Live"/> context and re-paints the dashboard
/// layout every <see cref="DashboardOptions.RefreshInterval"/>.
/// </summary>
/// <remarks>
/// <para>
/// The service is a no-op when the dashboard is disabled or stdout is
/// redirected (piped, captured to a file, running under a hook). In those
/// environments the <see cref="LoggingProgressReporter"/> still emits
/// structured logs — the dashboard is layered on top of, not instead of,
/// the log-based reporter.
/// </para>
/// <para>
/// Rendering exceptions are caught and logged: a broken terminal must not
/// take down the export.
/// </para>
/// </remarks>
public sealed class ConsoleDashboardHostedService : BackgroundService
{
    private readonly IDashboardStateSource _state;
    private readonly DashboardRenderer _renderer;
    private readonly DashboardOptions _options;
    private readonly IAnsiConsole _console;
    private readonly ILogger<ConsoleDashboardHostedService> _logger;

    public ConsoleDashboardHostedService(
        IDashboardStateSource state,
        DashboardRenderer renderer,
        DashboardOptions options,
        ILogger<ConsoleDashboardHostedService> logger,
        IAnsiConsole? console = null)
    {
        _state    = state;
        _renderer = renderer;
        _options  = options;
        _logger   = logger;
        _console  = console ?? AnsiConsole.Console;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!ShouldRun())
        {
            _logger.LogInformation("Console dashboard disabled (Enabled={Enabled}, redirected={Redirected}).",
                _options.Enabled, Console.IsOutputRedirected);
            return;
        }

        try
        {
            await _console.Live(_renderer.Build(_state.GetSnapshot()))
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .Cropping(VerticalOverflowCropping.Bottom)
                .StartAsync(async ctx =>
                {
                    using var timer = new PeriodicTimer(_options.RefreshInterval);
                    while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    {
                        try
                        {
                            var snapshot = _state.GetSnapshot();
                            ctx.UpdateTarget(_renderer.Build(snapshot));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(ex, "Dashboard render tick failed; continuing.");
                        }
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard terminated unexpectedly; falling back to log-based reporting.");
        }
    }

    private bool ShouldRun()
    {
        if (!_options.Enabled) return false;
        if (_options.DisableWhenOutputRedirected && Console.IsOutputRedirected) return false;
        return true;
    }
}
