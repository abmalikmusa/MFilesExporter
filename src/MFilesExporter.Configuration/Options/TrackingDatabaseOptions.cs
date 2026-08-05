namespace MFilesExporter.Configuration.Options;

/// <summary>
/// Connection configuration for the dedicated <c>MFilesExportTracking</c>
/// database. Kept separate from <see cref="MFilesSourceOptions"/> because
/// the two databases have different security boundaries: the vault is
/// read-only, the tracking DB is read/write to the exporter role only.
/// </summary>
public sealed class TrackingDatabaseOptions
{
    public const string SectionName = "Exporter:TrackingDatabase";

    /// <summary>ADO.NET connection string to the tracking DB.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Default command timeout applied to every SqlCommand.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Batch size when flushing metric / progress / error batches to the DB.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Max metric flush interval — flush a partial batch after this delay.</summary>
    public TimeSpan MetricFlushInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Value bound to sprocs' <c>@ActorName</c>; overrides <c>SUSER_SNAME()</c>
    /// when the calling identity is generic (e.g. a service principal).
    /// </summary>
    public string? ActorNameOverride { get; set; }
}
