using FluentValidation;
using MFilesExporter.Configuration.Options;

namespace MFilesExporter.Configuration.Validation;

public sealed class ExporterOptionsValidator : AbstractValidator<ExporterOptions>
{
    public ExporterOptionsValidator()
    {
        RuleFor(x => x.Source).NotNull().SetValidator(new MFilesSourceOptionsValidator());
        RuleFor(x => x.Storage).NotNull().SetValidator(new StorageOptionsValidator());
        RuleFor(x => x.Pipeline).NotNull().SetValidator(new PipelineOptionsValidator());
        RuleFor(x => x.StateStore).NotNull().SetValidator(new StateStoreOptionsValidator());
        RuleFor(x => x.TrackingDatabase).NotNull().SetValidator(new TrackingDatabaseOptionsValidator());
        RuleFor(x => x.BatchProcessing).NotNull().SetValidator(new BatchProcessingOptionsValidator());
        RuleFor(x => x.SqlStreaming).NotNull().SetValidator(new SqlStreamingOptionsValidator());
        RuleFor(x => x.FileExport).NotNull().SetValidator(new FileExportOptionsValidator());
        RuleFor(x => x.Metadata).NotNull().SetValidator(new MetadataOptionsValidator());
        RuleFor(x => x.Validation).NotNull().SetValidator(new ExportValidationOptionsValidator());
        RuleFor(x => x.Checkpoint).NotNull().SetValidator(new CheckpointOptionsValidator());
        RuleFor(x => x.RetryHandling).NotNull().SetValidator(new RetryHandlingOptionsValidator());
        RuleFor(x => x.Telemetry).NotNull().SetValidator(new TelemetryOptionsValidator());
        RuleFor(x => x.Dashboard).NotNull().SetValidator(new DashboardOptionsValidator());
    }
}

public sealed class MFilesSourceOptionsValidator : AbstractValidator<MFilesSourceOptions>
{
    public MFilesSourceOptionsValidator()
    {
        RuleFor(x => x.ConnectionString).NotEmpty();
        RuleFor(x => x.CommandTimeoutSeconds).GreaterThan(0);
        RuleFor(x => x.EnumerationBatchSize).InclusiveBetween(50, 100_000);
        RuleFor(x => x.PartitionKey).NotEmpty();
        RuleFor(x => x.Tables.DocumentFileVersion).NotEmpty();
        RuleFor(x => x.Tables.DataFileVersion).NotEmpty();
        RuleFor(x => x.Tables.DataFileVersionBytes).NotEmpty();
    }
}

public sealed class StorageOptionsValidator : AbstractValidator<StorageOptions>
{
    public StorageOptionsValidator()
    {
        RuleFor(x => x.RootPath).NotEmpty();
        RuleFor(x => x.ManifestPath).NotEmpty();
        RuleFor(x => x.ShardDepth).InclusiveBetween(1, 4);
        RuleFor(x => x.WriteBufferSize).GreaterThanOrEqualTo(4_096);
        RuleFor(x => x.ManifestRotationEntryCount).GreaterThan(0);
        RuleFor(x => x.MinimumFreeSpaceGb).GreaterThanOrEqualTo(0);
    }
}

public sealed class PipelineOptionsValidator : AbstractValidator<PipelineOptions>
{
    public PipelineOptionsValidator()
    {
        RuleFor(x => x.EnumerationChannelCapacity).GreaterThan(0);
        RuleFor(x => x.ContentChannelCapacity).GreaterThan(0);
        RuleFor(x => x.ContentReaderConcurrency).InclusiveBetween(1, 256);
        RuleFor(x => x.SinkConcurrency).InclusiveBetween(1, 256);
        RuleFor(x => x.OutcomeBatchSize).InclusiveBetween(1, 10_000);
        RuleFor(x => x.ProgressReportInterval).Must(t => t > TimeSpan.Zero);
        RuleFor(x => x.CheckpointFlushInterval).Must(t => t > TimeSpan.Zero);
    }
}

public sealed class StateStoreOptionsValidator : AbstractValidator<StateStoreOptions>
{
    public StateStoreOptionsValidator()
    {
        RuleFor(x => x.Provider).NotEmpty();
        RuleFor(x => x.ConnectionString).NotEmpty();
    }
}
