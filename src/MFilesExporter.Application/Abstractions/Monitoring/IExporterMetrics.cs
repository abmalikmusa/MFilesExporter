namespace MFilesExporter.Application.Abstractions.Monitoring;

/// <summary>
/// Application-facing surface over the monitoring meter. Callers hand over
/// business events (a document was exported, a SQL round-trip took N ms) and
/// the implementation translates them into OpenTelemetry instruments.
/// </summary>
/// <remarks>
/// The intent is a stable façade that outlives changes in the underlying
/// meter names or tags. Domain code depends on this interface; only the
/// telemetry adapter references <see cref="System.Diagnostics.Metrics.Meter"/>.
/// </remarks>
public interface IExporterMetrics
{
    /// <summary>Signals one document read from the source enumeration.</summary>
    void RecordEnumerated(long count = 1);

    /// <summary>Records a terminal document outcome (Succeeded / Failed / Skipped).</summary>
    void RecordOutcome(DocumentOutcome outcome, long bytesWritten, TimeSpan elapsed);

    /// <summary>Records the latency of a single SQL round-trip, tagged with the logical operation.</summary>
    void RecordSqlLatency(string operation, TimeSpan elapsed, bool succeeded);

    /// <summary>Records the latency of a sink write.</summary>
    void RecordSinkLatency(TimeSpan elapsed, bool succeeded);

    /// <summary>Increments the retry counter for a named operation.</summary>
    void RecordRetry(string operation, string category);

    /// <summary>Records a checkpoint flush event.</summary>
    void RecordCheckpointFlush(TimeSpan elapsed, long recordsFlushed);
}

/// <summary>Coarse-grained terminal outcome for a single exported document.</summary>
public enum DocumentOutcome
{
    Succeeded,
    Failed,
    Skipped,
}
