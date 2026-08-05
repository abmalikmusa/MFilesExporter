namespace MFilesExporter.Configuration.Options;

public sealed class TelemetryOptions
{
    public const string SectionName = "Exporter:Telemetry";

    public string ServiceName { get; set; } = "mfiles-exporter";
    public string ServiceNamespace { get; set; } = "seamfix";
    public string ServiceVersion { get; set; } = "1.0.0";
    public bool EnablePrometheusEndpoint { get; set; } = true;
    public string PrometheusListenerUrl { get; set; } = "http://+:9464/";
    public bool EnableOtlpExporter { get; set; }
    public string? OtlpEndpoint { get; set; }
    public double TraceSamplingRatio { get; set; } = 0.05;
}
