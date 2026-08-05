using System.Diagnostics.Metrics;
using MFilesExporter.Application.Abstractions.Monitoring;

namespace MFilesExporter.Infrastructure.Monitoring;

/// <summary>
/// Central telemetry adapter. Owns a single <see cref="Meter"/> that publishes
/// every business-level metric under the <c>MFilesExporter</c> namespace.
/// Registered as a singleton and disposed with the host.
/// </summary>
/// <remarks>
/// <para>
/// The meter name is exposed via <see cref="MeterName"/> so
/// <see cref="OpenTelemetry.Metrics.MeterProviderBuilder.AddMeter(string[])"/>
/// can subscribe without a hard reference to this class.
/// </para>
/// <para>
/// Instruments follow OpenTelemetry semantic conventions:
/// counters are named as verbs (<c>documents.exported</c>), histograms carry
/// a unit suffix (<c>_ms</c>), and gauges are updated via <see cref="ObservableGauge{T}"/>.
/// </para>
/// </remarks>
public sealed class ExporterMetrics : IExporterMetrics, IDisposable
{
    public const string MeterName    = "MFilesExporter.Monitoring";
    public const string MeterVersion = "1.0.0";

    private readonly Meter _meter;

    // Counters — monotonic since process start.
    private readonly Counter<long> _enumerated;
    private readonly Counter<long> _succeeded;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _skipped;
    private readonly Counter<long> _bytesWritten;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _checkpointsFlushed;

    // Histograms — per-operation latency distributions.
    private readonly Histogram<double> _documentDuration;
    private readonly Histogram<double> _sqlLatency;
    private readonly Histogram<double> _sinkLatency;
    private readonly Histogram<double> _checkpointLatency;

    public ExporterMetrics()
    {
        _meter = new Meter(MeterName, MeterVersion);

        _enumerated = _meter.CreateCounter<long>(
            "mfilesexporter.documents.enumerated", unit: "{document}",
            description: "Documents read from source enumeration.");

        _succeeded = _meter.CreateCounter<long>(
            "mfilesexporter.documents.succeeded", unit: "{document}",
            description: "Terminal Succeeded outcomes.");

        _failed = _meter.CreateCounter<long>(
            "mfilesexporter.documents.failed", unit: "{document}",
            description: "Terminal Failed outcomes.");

        _skipped = _meter.CreateCounter<long>(
            "mfilesexporter.documents.skipped", unit: "{document}",
            description: "Terminal Skipped outcomes.");

        _bytesWritten = _meter.CreateCounter<long>(
            "mfilesexporter.bytes.written", unit: "By",
            description: "Bytes written to durable sinks.");

        _retries = _meter.CreateCounter<long>(
            "mfilesexporter.retries.total", unit: "{retry}",
            description: "Retry attempts observed by the retry executor.");

        _checkpointsFlushed = _meter.CreateCounter<long>(
            "mfilesexporter.checkpoints.flushed", unit: "{checkpoint}",
            description: "Checkpoint flushes committed.");

        _documentDuration = _meter.CreateHistogram<double>(
            "mfilesexporter.document.duration", unit: "ms",
            description: "End-to-end time to process one document.");

        _sqlLatency = _meter.CreateHistogram<double>(
            "mfilesexporter.sql.latency", unit: "ms",
            description: "Latency of individual SQL operations.");

        _sinkLatency = _meter.CreateHistogram<double>(
            "mfilesexporter.sink.latency", unit: "ms",
            description: "Latency of sink writes (file / storage layer).");

        _checkpointLatency = _meter.CreateHistogram<double>(
            "mfilesexporter.checkpoint.latency", unit: "ms",
            description: "Time to flush a checkpoint.");
    }

    internal Meter Meter => _meter;

    public void RecordEnumerated(long count = 1) => _enumerated.Add(count);

    public void RecordOutcome(DocumentOutcome outcome, long bytesWritten, TimeSpan elapsed)
    {
        var counter = outcome switch
        {
            DocumentOutcome.Succeeded => _succeeded,
            DocumentOutcome.Failed    => _failed,
            DocumentOutcome.Skipped   => _skipped,
            _                         => _skipped,
        };
        counter.Add(1);

        if (bytesWritten > 0) _bytesWritten.Add(bytesWritten);

        _documentDuration.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("outcome", outcome.ToString()));
    }

    public void RecordSqlLatency(string operation, TimeSpan elapsed, bool succeeded)
    {
        _sqlLatency.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("status",    succeeded ? "success" : "failed"));
    }

    public void RecordSinkLatency(TimeSpan elapsed, bool succeeded)
    {
        _sinkLatency.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("status", succeeded ? "success" : "failed"));
    }

    public void RecordRetry(string operation, string category)
    {
        _retries.Add(1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("category",  category));
    }

    public void RecordCheckpointFlush(TimeSpan elapsed, long recordsFlushed)
    {
        _checkpointsFlushed.Add(1);
        _checkpointLatency.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("records", recordsFlushed));
    }

    public void Dispose() => _meter.Dispose();
}
