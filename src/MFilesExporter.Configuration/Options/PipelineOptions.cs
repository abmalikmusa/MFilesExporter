namespace MFilesExporter.Configuration.Options;

public sealed class PipelineOptions
{
    public const string SectionName = "Exporter:Pipeline";

    public int EnumerationChannelCapacity { get; set; } = 5_000;
    public int ContentChannelCapacity { get; set; } = 128;
    public int ContentReaderConcurrency { get; set; } = 8;
    public int SinkConcurrency { get; set; } = 8;
    public TimeSpan ProgressReportInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan CheckpointFlushInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int OutcomeBatchSize { get; set; } = 200;
    public TimeSpan OutcomeBatchFlushInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int MaxDocumentSizeMb { get; set; }
}
