namespace MFilesExporter.Configuration.Options;

/// <summary>
/// Configuration for the Spectre.Console real-time dashboard.
/// Section: <c>Exporter:Dashboard</c>.
/// </summary>
public sealed class DashboardOptions
{
    public const string SectionName = "Exporter:Dashboard";

    /// <summary>Master switch. Off in containers/CI where TTY is absent.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Frame cadence. Values below 250 ms cause flicker on slow terminals.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Maximum worker rows to render. Extra workers collapse into a "+N more" footer.</summary>
    public int MaxWorkerRows { get; set; } = 16;

    /// <summary>Truncate long document keys to keep the workers table on a single line.</summary>
    public int MaxDocumentKeyLength { get; set; } = 48;

    /// <summary>Auto-disable when stdout is redirected (piped or captured to a file).</summary>
    public bool DisableWhenOutputRedirected { get; set; } = true;
}
