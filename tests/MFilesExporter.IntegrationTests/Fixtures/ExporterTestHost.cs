using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.DependencyInjection;
using MFilesExporter.Configuration.DependencyInjection;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Export.DependencyInjection;
using MFilesExporter.Infrastructure.DependencyInjection;
using MFilesExporter.Logging.DependencyInjection;
using MFilesExporter.Persistence.DependencyInjection;
using MFilesExporter.Reporting.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MFilesExporter.IntegrationTests.Fixtures;

/// <summary>
/// Builds a Generic Host configured exactly like <c>Program.cs</c> but with
/// its connection strings + output paths pointed at a temp directory and a
/// Testcontainers SQL Server. Test-only concerns are isolated:
/// <list type="bullet">
///   <item><description>Prometheus / OTLP exporters are off (would open sockets).</description></item>
///   <item><description>Dashboard is off (no TTY under xUnit).</description></item>
///   <item><description>Windows-Service integration is not called.</description></item>
/// </list>
/// </summary>
public sealed class ExporterTestHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly bool _ownsOutputRoot;

    public string OutputRoot { get; }
    public string PartitionKey { get; }

    private ExporterTestHost(IHost host, string outputRoot, string partitionKey, bool ownsOutputRoot)
    {
        _host = host;
        OutputRoot = outputRoot;
        PartitionKey = partitionKey;
        _ownsOutputRoot = ownsOutputRoot;
    }

    public IServiceProvider Services => _host.Services;

    public IExportPipeline Pipeline => _host.Services.GetRequiredService<IExportPipeline>();

    /// <summary>
    /// Creates a host with a fresh temp output root. The host owns the
    /// directory and deletes it on <see cref="DisposeAsync"/>.
    /// </summary>
    public static ExporterTestHost Create(
        SqlServerFixture sql,
        int workerCount = 4,
        string partitionKey = "test",
        Action<IServiceCollection>? customize = null)
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "mfilesexporter-it-" + Guid.NewGuid().ToString("N"));
        return CreateInternal(sql, workerCount, partitionKey, outputRoot, ownsOutputRoot: true, customize);
    }

    /// <summary>
    /// Creates a host bound to an existing output root — used by the resume
    /// test to prove that a second process picks up where the first left off.
    /// Caller owns cleanup.
    /// </summary>
    public static ExporterTestHost CreateSharing(SqlServerFixture sql, string outputRoot, int workerCount, string partitionKey)
    {
        return CreateInternal(sql, workerCount, partitionKey, outputRoot, ownsOutputRoot: false, customize: null);
    }

    private static ExporterTestHost CreateInternal(
        SqlServerFixture sql, int workerCount, string partitionKey, string outputRoot, bool ownsOutputRoot,
        Action<IServiceCollection>? customize = null)
    {
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(Path.Combine(outputRoot, "documents"));
        Directory.CreateDirectory(Path.Combine(outputRoot, "metadata"));
        Directory.CreateDirectory(Path.Combine(outputRoot, "manifests"));
        Directory.CreateDirectory(Path.Combine(outputRoot, "checkpoints"));

        var configValues = new Dictionary<string, string?>
        {
            ["Exporter:Source:ConnectionString"]                 = sql.SourceConnectionString,
            ["Exporter:Source:PartitionKey"]                     = partitionKey,
            ["Exporter:Source:EnumerationBatchSize"]             = "50",
            ["Exporter:TrackingDatabase:ConnectionString"]       = sql.TrackingConnectionString,
            ["Exporter:StateStore:ConnectionString"]             = Path.Combine(outputRoot, "state.db"),
            ["Exporter:Storage:RootPath"]                        = Path.Combine(outputRoot, "documents"),
            ["Exporter:Storage:ManifestPath"]                    = Path.Combine(outputRoot, "manifests"),
            ["Exporter:Storage:MinimumFreeSpaceGb"]              = "0",
            ["Exporter:FileExport:RootPath"]                     = Path.Combine(outputRoot, "documents"),
            ["Exporter:FileExport:FolderStrategy"]               = "HashSharded",
            ["Exporter:FileExport:ShardDepth"]                   = "2",
            ["Exporter:FileExport:FsyncOnWrite"]                 = "false",
            ["Exporter:Metadata:OutputDirectory"]                = Path.Combine(outputRoot, "metadata"),
            ["Exporter:Checkpoint:WalDirectory"]                 = Path.Combine(outputRoot, "checkpoints"),
            ["Exporter:Checkpoint:FsyncOnWrite"]                 = "false",
            ["Exporter:Checkpoint:PersistToTrackingDb"]          = "true",
            ["Exporter:Pipeline:ContentReaderConcurrency"]       = workerCount.ToString(),
            ["Exporter:Pipeline:SinkConcurrency"]                = workerCount.ToString(),
            ["Exporter:Pipeline:EnumerationChannelCapacity"]     = "200",
            ["Exporter:Pipeline:ContentChannelCapacity"]         = "32",
            ["Exporter:Pipeline:OutcomeBatchSize"]               = "5",
            ["Exporter:Pipeline:OutcomeBatchFlushInterval"]      = "00:00:00.500",
            ["Exporter:Pipeline:CheckpointFlushInterval"]        = "00:00:00.500",
            ["Exporter:ParallelProcessing:WorkerCount"]          = workerCount.ToString(),
            ["Exporter:ParallelProcessing:ChannelCapacity"]      = "64",
            ["Exporter:Dashboard:Enabled"]                       = "false",
            ["Exporter:Telemetry:EnablePrometheusEndpoint"]      = "false",
            ["Exporter:Telemetry:EnableOtlpExporter"]            = "false",
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Configuration.AddConfiguration(config);

        builder.Services.AddExporterConfiguration(builder.Configuration);
        builder.Services.AddExporterLogging();
        builder.Services.AddLogging();

        var telemetry = builder.Configuration
            .GetSection(ExporterOptions.SectionName)
            .Get<ExporterOptions>()?.Telemetry ?? new TelemetryOptions();

        builder.Services.AddExporterInfrastructure(telemetry);
        builder.Services.AddExporterPersistence();
        builder.Services.AddExporterExport();
        builder.Services.AddExporterReporting();
        builder.Services.AddExporterApplication();

        // Test-only decoration — fault-injection tests replace the sink here.
        customize?.Invoke(builder.Services);

        var host = builder.Build();
        return new ExporterTestHost(host, outputRoot, partitionKey, ownsOutputRoot);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync().ConfigureAwait(false);
        _host.Dispose();
        if (_ownsOutputRoot)
        {
            try { Directory.Delete(OutputRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}
