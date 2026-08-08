namespace MFilesExporter.Configuration.Options;

/// <summary>
/// Top-level composite options object bound to the "Exporter" section.
/// </summary>
public sealed class ExporterOptions
{
    public const string SectionName = "Exporter";

    public MFilesSourceOptions Source { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public PipelineOptions Pipeline { get; set; } = new();
    public StateStoreOptions StateStore { get; set; } = new();
    public TrackingDatabaseOptions TrackingDatabase { get; set; } = new();
    public BatchProcessingOptions BatchProcessing { get; set; } = new();
    public SqlStreamingOptions SqlStreaming { get; set; } = new();
    public FileExportOptions FileExport { get; set; } = new();
    public MetadataOptions Metadata { get; set; } = new();
    public ExportValidationOptions Validation { get; set; } = new();
    public CheckpointOptions Checkpoint { get; set; } = new();
    public ParallelProcessingOptions ParallelProcessing { get; set; } = new();
    public RetryHandlingOptions RetryHandling { get; set; } = new();
    public TelemetryOptions Telemetry { get; set; } = new();
    public DashboardOptions Dashboard { get; set; } = new();
}
