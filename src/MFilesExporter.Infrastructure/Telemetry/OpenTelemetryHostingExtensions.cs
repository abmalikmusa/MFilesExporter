using MFilesExporter.Configuration.Options;
using MFilesExporter.Infrastructure.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MFilesExporter.Infrastructure.Telemetry;

public static class OpenTelemetryHostingExtensions
{
    public const string PipelineMeterName = "MFilesExporter.Pipeline";
    public const string PipelineActivitySourceName = "MFilesExporter.Pipeline";

    public static IServiceCollection AddExporterOpenTelemetry(this IServiceCollection services, TelemetryOptions telemetry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(telemetry);

        services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(
                    serviceName: telemetry.ServiceName,
                    serviceNamespace: telemetry.ServiceNamespace,
                    serviceVersion: telemetry.ServiceVersion)
                .AddAttributes(new KeyValuePair<string, object>[]
                {
                    new("host.name", Environment.MachineName),
                }))
            .WithMetrics(m =>
            {
                // Business metrics (this project) — everything requested:
                // export speed, docs exported, queue depth, worker utilization,
                // memory, CPU, disk, SQL latency, retries, failures, ETA.
                m.AddMeter(PipelineMeterName);
                m.AddMeter(ExporterMetrics.MeterName);
                m.AddMeter("MFilesExporter.Retry");

                // Runtime instrumentation gives us memory, CPU, GC, thread-pool.
                m.AddRuntimeInstrumentation();

                if (telemetry.EnablePrometheusEndpoint)
                {
                    m.AddPrometheusHttpListener(o =>
                    {
                        o.UriPrefixes = new[] { telemetry.PrometheusListenerUrl };
                    });
                }
                if (telemetry.EnableOtlpExporter && !string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint))
                {
                    m.AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.OtlpEndpoint));
                }
            })
            .WithTracing(t =>
            {
                t.AddSource(PipelineActivitySourceName);
                if (telemetry.EnableOtlpExporter && !string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint))
                {
                    t.AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.OtlpEndpoint));
                }
            });

        return services;
    }
}
