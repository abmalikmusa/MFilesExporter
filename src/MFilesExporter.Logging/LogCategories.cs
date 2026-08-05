namespace MFilesExporter.Logging;

/// <summary>
/// Canonical <c>Category</c> property values attached to log events.
/// Serilog sinks use these to route events to dedicated files:
/// audit → <c>audit-.log</c>, performance → <c>performance-.log</c>, etc.
/// </summary>
public static class LogCategories
{
    /// <summary>Application / lifecycle logs. Default when unset.</summary>
    public const string Application = "Application";

    /// <summary>Audit-trail: who did what, when, and to what.</summary>
    public const string Audit = "Audit";

    /// <summary>Performance / latency measurements.</summary>
    public const string Performance = "Performance";

    /// <summary>Per-worker diagnostics tagged with a worker id.</summary>
    public const string Worker = "Worker";

    /// <summary>Serilog property name carrying the category.</summary>
    public const string PropertyName = "Category";
}
