using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MFilesExporter.Export.Telemetry;

public static class PipelineTelemetry
{
    public const string ActivitySourceName = "MFilesExporter.Pipeline";
    public const string MeterName = "MFilesExporter.Pipeline";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> DocumentsEnumerated =
        Meter.CreateCounter<long>("mfilesexporter.documents.enumerated", unit: "{documents}");

    public static readonly Counter<long> DocumentsSucceeded =
        Meter.CreateCounter<long>("mfilesexporter.documents.succeeded", unit: "{documents}");

    public static readonly Counter<long> DocumentsFailed =
        Meter.CreateCounter<long>("mfilesexporter.documents.failed", unit: "{documents}");

    public static readonly Counter<long> DocumentsSkipped =
        Meter.CreateCounter<long>("mfilesexporter.documents.skipped", unit: "{documents}");

    public static readonly Counter<long> BytesWritten =
        Meter.CreateCounter<long>("mfilesexporter.bytes.written", unit: "By");

    public static readonly Histogram<double> DocumentDurationMs =
        Meter.CreateHistogram<double>("mfilesexporter.document.duration", unit: "ms");
}
