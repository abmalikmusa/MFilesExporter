using System.Globalization;
using MFilesExporter.Application.Abstractions.Dashboard;
using MFilesExporter.Configuration.Options;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MFilesExporter.Reporting.Dashboard;

/// <summary>
/// Pure renderer — takes a <see cref="DashboardSnapshot"/> and produces the
/// Spectre <see cref="Layout"/> tree that <c>AnsiConsole.Live(...)</c> paints.
/// Extracted from the hosted service so unit tests can materialise a frame
/// and compare its structure without spinning a Live context.
/// </summary>
public sealed class DashboardRenderer
{
    private readonly DashboardOptions _options;

    public DashboardRenderer(DashboardOptions options) => _options = options;

    public Layout Build(DashboardSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);

        var layout = new Layout("root")
            .SplitRows(
                new Layout("header").Size(3),
                new Layout("body").SplitColumns(
                    new Layout("left").Ratio(3),
                    new Layout("right").Ratio(2)),
                new Layout("workers").Size(_options.MaxWorkerRows + 4),
                new Layout("footer").Size(3));

        layout["header"].Update(BuildHeader(s));

        layout["left"].SplitRows(
            new Layout("progress").Size(6),
            new Layout("throughput").Size(6),
            new Layout("current").Size(6));

        layout["right"].SplitRows(
            new Layout("counts").Size(9),
            new Layout("resources").Size(9));

        layout["progress"].Update(BuildProgressPanel(s));
        layout["throughput"].Update(BuildThroughputPanel(s));
        layout["current"].Update(BuildCurrentPanel(s));
        layout["counts"].Update(BuildCountsPanel(s));
        layout["resources"].Update(BuildResourcesPanel(s));
        layout["workers"].Update(BuildWorkersPanel(s));
        layout["footer"].Update(BuildFooter(s));

        return layout;
    }

    // ------------------------------------------------------------------
    // Panels
    // ------------------------------------------------------------------

    private static IRenderable BuildHeader(DashboardSnapshot s)
    {
        var title = new Markup("[bold cyan]MFilesExporter[/]  [grey]— Enterprise Document Export Dashboard[/]");
        var elapsed = new Markup($"[grey]elapsed[/] [white]{FormatElapsed(s.Elapsed)}[/]");
        var eta = new Markup(s.EtaSeconds is null
            ? "[grey]ETA[/] [dim]—[/]"
            : $"[grey]ETA[/] [green]{FormatElapsed(TimeSpan.FromSeconds(s.EtaSeconds.Value))}[/]");

        var grid = new Grid()
            .AddColumn(new GridColumn())
            .AddColumn(new GridColumn().RightAligned())
            .AddColumn(new GridColumn().RightAligned())
            .AddRow(title, elapsed, eta);

        return new Panel(grid)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Padding(1, 0, 1, 0);
    }

    private static IRenderable BuildProgressPanel(DashboardSnapshot s)
    {
        var pct = s.TotalExpected > 0
            ? Math.Clamp((double)s.TotalProcessed / s.TotalExpected * 100.0, 0.0, 100.0)
            : 0.0;

        var bar = new BarChart()
            .Width(50)
            .AddItem("progress", pct, PctColor(pct));

        var text = new Markup(
            s.TotalExpected > 0
                ? $"[bold]{s.TotalProcessed:N0}[/] / [grey]{s.TotalExpected:N0}[/]   [dim]({pct:F1}%)[/]"
                : $"[bold]{s.TotalProcessed:N0}[/] / [dim]?[/]");

        var remaining = new Markup(
            s.TotalExpected > 0
                ? $"[grey]remaining[/] [white]{s.Remaining:N0}[/]"
                : "[grey]remaining[/] [dim]—[/]");

        var grid = new Grid()
            .AddColumn()
            .AddRow(bar)
            .AddRow(text)
            .AddRow(remaining);

        return new Panel(grid)
            .Header(" [bold]Progress[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey37);
    }

    private static IRenderable BuildThroughputPanel(DashboardSnapshot s)
    {
        var docsPerSec = new Markup($"[bold green]{s.DocumentsPerSecond,7:F1}[/] [grey]docs/s[/]");
        var mbPerSec   = new Markup($"[bold yellow]{s.MegabytesPerSecond,7:F2}[/] [grey]MiB/s[/]");
        var bytes      = new Markup($"[grey]written[/] [white]{FormatBytes(s.TotalBytesWritten)}[/]");
        var eta        = new Markup(s.EtaSeconds is null
            ? "[grey]ETA[/]     [dim]—[/]"
            : $"[grey]ETA[/]     [green]{FormatElapsed(TimeSpan.FromSeconds(s.EtaSeconds.Value))}[/]");

        var grid = new Grid()
            .AddColumn()
            .AddRow(docsPerSec)
            .AddRow(mbPerSec)
            .AddRow(bytes)
            .AddRow(eta);

        return new Panel(grid)
            .Header(" [bold]Throughput[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey37);
    }

    private IRenderable BuildCurrentPanel(DashboardSnapshot s)
    {
        var busy = s.Workers.FirstOrDefault(w => w.State == WorkerActivityState.Busy);
        var docLine = busy is null
            ? "[dim]waiting for work…[/]"
            : $"[grey]worker-{busy.WorkerId}[/] [white]{Truncate(busy.CurrentDocumentKey ?? "?", _options.MaxDocumentKeyLength)}[/]";

        var grid = new Grid()
            .AddColumn()
            .AddRow(new Markup("[grey]current document[/]"))
            .AddRow(new Markup(docLine));

        return new Panel(grid)
            .Header(" [bold]Current Activity[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey37);
    }

    private static IRenderable BuildCountsPanel(DashboardSnapshot s)
    {
        var g = new Grid()
            .AddColumn(new GridColumn().PadRight(1))
            .AddColumn(new GridColumn().RightAligned())
            .AddRow(new Markup("[grey]succeeded[/]"),        new Markup($"[green]{s.TotalSucceeded,12:N0}[/]"))
            .AddRow(new Markup("[grey]failed[/]"),           new Markup($"[red]{s.TotalFailed,12:N0}[/]"))
            .AddRow(new Markup("[grey]skipped[/]"),          new Markup($"[yellow]{s.TotalSkipped,12:N0}[/]"))
            .AddRow(new Markup("[grey]retries[/]"),          new Markup($"[magenta]{s.TotalRetries,12:N0}[/]"))
            .AddRow(new Markup("[grey]processed[/]"),        new Markup($"[white]{s.TotalProcessed,12:N0}[/]"))
            .AddRow(new Markup("[grey]remaining[/]"),        new Markup($"[white]{s.Remaining,12:N0}[/]"))
            .AddRow(new Markup("[grey]bytes written[/]"),    new Markup($"[white]{FormatBytes(s.TotalBytesWritten),12}[/]"));

        return new Panel(g)
            .Header(" [bold]Counts[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey37);
    }

    private static IRenderable BuildResourcesPanel(DashboardSnapshot s)
    {
        var g = new Grid()
            .AddColumn(new GridColumn().PadRight(1))
            .AddColumn(new GridColumn().RightAligned())
            .AddRow(new Markup("[grey]cpu[/]"),               new Markup($"[white]{s.CpuUsagePercent,12:F1}%[/]"))
            .AddRow(new Markup("[grey]memory[/]"),            new Markup($"[white]{FormatBytes(s.ProcessMemoryBytes),12}[/]"))
            .AddRow(new Markup("[grey]disk free[/]"),         new Markup($"[white]{FormatBytes(s.DiskFreeBytes),12}[/]"))
            .AddRow(new Markup("[grey]docs/sec[/]"),          new Markup($"[green]{s.DocumentsPerSecond,12:F1}[/]"))
            .AddRow(new Markup("[grey]MiB/sec[/]"),           new Markup($"[yellow]{s.MegabytesPerSecond,12:F2}[/]"))
            .AddRow(new Markup("[grey]workers busy[/]"),      new Markup($"[white]{s.Workers.Count(w => w.State == WorkerActivityState.Busy),12}[/]"))
            .AddRow(new Markup("[grey]uptime[/]"),            new Markup($"[white]{FormatElapsed(s.Elapsed),12}[/]"));

        return new Panel(g)
            .Header(" [bold]Resources[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey37);
    }

    private IRenderable BuildWorkersPanel(DashboardSnapshot s)
    {
        var table = new Table()
            .Border(TableBorder.SimpleHeavy)
            .BorderColor(Color.Grey37)
            .Expand()
            .AddColumn(new TableColumn("[bold]#[/]").Width(4).RightAligned())
            .AddColumn(new TableColumn("[bold]State[/]").Width(9))
            .AddColumn(new TableColumn("[bold]Current document[/]"))
            .AddColumn(new TableColumn("[bold]Batch[/]").Width(14))
            .AddColumn(new TableColumn("[bold]Bytes[/]").Width(10).RightAligned())
            .AddColumn(new TableColumn("[bold]Done[/]").Width(8).RightAligned())
            .AddColumn(new TableColumn("[bold]Fail[/]").Width(6).RightAligned());

        var visible = s.Workers.Take(_options.MaxWorkerRows);
        foreach (var w in visible)
        {
            var stateMarkup = w.State switch
            {
                WorkerActivityState.Busy     => "[green]● busy[/]",
                WorkerActivityState.Idle     => "[yellow]○ idle[/]",
                WorkerActivityState.Finished => "[grey]◌ done[/]",
                _                            => "[dim]?[/]",
            };

            var doc = w.CurrentDocumentKey ?? (w.State == WorkerActivityState.Busy ? "?" : "—");
            var docCol = w.State == WorkerActivityState.Busy
                ? $"[white]{Truncate(doc, _options.MaxDocumentKeyLength)}[/]"
                : $"[dim]{Truncate(doc, _options.MaxDocumentKeyLength)}[/]";

            table.AddRow(
                w.WorkerId.ToString(CultureInfo.InvariantCulture),
                stateMarkup,
                docCol,
                w.CurrentBatchId ?? "[dim]—[/]",
                FormatBytes(w.BytesWritten),
                w.DocumentsProcessed.ToString(CultureInfo.InvariantCulture),
                w.DocumentsFailed > 0
                    ? $"[red]{w.DocumentsFailed}[/]"
                    : "[dim]0[/]");
        }

        if (s.Workers.Count > _options.MaxWorkerRows)
        {
            var more = s.Workers.Count - _options.MaxWorkerRows;
            table.Caption(new TableTitle($"[dim]+{more} more worker(s) not shown[/]"));
        }

        return new Panel(table)
            .Header($" [bold]Workers[/] [dim]({s.Workers.Count})[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey37);
    }

    private static IRenderable BuildFooter(DashboardSnapshot s)
    {
        var left = new Markup($"[dim]started {s.StartedAtUtc:yyyy-MM-dd HH:mm:ss} UTC[/]");
        var right = new Markup($"[dim]{DateTimeOffset.UtcNow:HH:mm:ss} — press Ctrl+C to stop[/]");
        var grid = new Grid()
            .AddColumn(new GridColumn())
            .AddColumn(new GridColumn().RightAligned())
            .AddRow(left, right);
        return new Panel(grid)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey37);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Color PctColor(double pct) => pct switch
    {
        >= 90 => Color.Green,
        >= 50 => Color.Yellow,
        _     => Color.Cyan1,
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        const double kib = 1024, mib = kib * 1024, gib = mib * 1024, tib = gib * 1024;
        return bytes switch
        {
            >= (long)tib => $"{bytes / tib:F2} TiB",
            >= (long)gib => $"{bytes / gib:F2} GiB",
            >= (long)mib => $"{bytes / mib:F1} MiB",
            >= (long)kib => $"{bytes / kib:F0} KiB",
            _            => $"{bytes} B",
        };
    }

    private static string FormatElapsed(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t:hh\\:mm\\:ss}";
        return t.ToString("hh\\:mm\\:ss");
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return string.Concat(s.AsSpan(0, Math.Max(1, max - 1)), "…");
    }
}
