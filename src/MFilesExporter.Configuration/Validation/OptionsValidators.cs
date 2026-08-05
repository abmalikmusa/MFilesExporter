using FluentValidation;
using MFilesExporter.Configuration.Options;

namespace MFilesExporter.Configuration.Validation;

/// <summary>
/// Batch-processing settings validator. Enforces sensible bounds on the two
/// numbers most likely to be mis-tuned: <c>BatchSize</c> and
/// <c>MaxParallelismPerBatch</c>.
/// </summary>
public sealed class BatchProcessingOptionsValidator : AbstractValidator<BatchProcessingOptions>
{
    public BatchProcessingOptionsValidator()
    {
        RuleFor(x => x.BatchSize).InclusiveBetween(1, 100_000);
        RuleFor(x => x.MaxParallelismPerBatch).InclusiveBetween(1, 512);
        RuleFor(x => x.BatchTimeout).Must(t => t > TimeSpan.Zero)
            .WithMessage("BatchTimeout must be greater than zero.");
        RuleFor(x => x.PauseBetweenBatches).Must(t => t >= TimeSpan.Zero)
            .WithMessage("PauseBetweenBatches cannot be negative.");
        RuleFor(x => x.FailureRateThreshold).InclusiveBetween(0.0, 1.0);
    }
}

public sealed class CheckpointOptionsValidator : AbstractValidator<CheckpointOptions>
{
    public CheckpointOptionsValidator()
    {
        RuleFor(x => x.WalDirectory).NotEmpty();
        RuleFor(x => x.SqlSaveTimeout).Must(t => t > TimeSpan.Zero)
            .WithMessage("SqlSaveTimeout must be greater than zero.");
    }
}

public sealed class ExportValidationOptionsValidator : AbstractValidator<ExportValidationOptions>
{
    public ExportValidationOptionsValidator()
    {
        RuleFor(x => x.PerValidatorTimeout).Must(t => t > TimeSpan.Zero)
            .WithMessage("PerValidatorTimeout must be greater than zero.");
    }
}

public sealed class FileExportOptionsValidator : AbstractValidator<FileExportOptions>
{
    public FileExportOptionsValidator()
    {
        RuleFor(x => x.RootPath).NotEmpty();
        RuleFor(x => x.ShardDepth).InclusiveBetween(1, 4);
        RuleFor(x => x.NumericBucketCount).GreaterThan(0);
        RuleFor(x => x.DateFolderPattern).NotEmpty();
        RuleFor(x => x.MaxFilenameLength).InclusiveBetween(16, 255);
        RuleFor(x => x.MaxFullPathLength).InclusiveBetween(64, 32_767);
        RuleFor(x => x.WriteBufferSize).GreaterThanOrEqualTo(4_096);
        RuleFor(x => x.DefaultTitle).NotEmpty();
    }
}

public sealed class MetadataOptionsValidator : AbstractValidator<MetadataOptions>
{
    public MetadataOptionsValidator()
    {
        RuleFor(x => x.OutputDirectory).NotEmpty();
        RuleFor(x => x.CsvFileName).NotEmpty();
        RuleFor(x => x.JsonFileName).NotEmpty();
        RuleFor(x => x.ManifestFileName).NotEmpty();
        RuleFor(x => x.CsvDelimiter).NotEmpty();
        RuleFor(x => x.FlushEveryNRecords).GreaterThan(0);
    }
}

public sealed class ParallelProcessingOptionsValidator : AbstractValidator<ParallelProcessingOptions>
{
    public ParallelProcessingOptionsValidator()
    {
        RuleFor(x => x.WorkerCount).InclusiveBetween(1, 512);
        RuleFor(x => x.ChannelCapacity).GreaterThan(0);
        RuleFor(x => x.HeartbeatInterval).Must(t => t > TimeSpan.Zero)
            .WithMessage("HeartbeatInterval must be greater than zero.");
        RuleFor(x => x.StalledThreshold).Must(t => t > TimeSpan.Zero)
            .WithMessage("StalledThreshold must be greater than zero.");
        RuleFor(x => x.GracefulShutdownTimeout).Must(t => t >= TimeSpan.Zero)
            .WithMessage("GracefulShutdownTimeout cannot be negative.");
        RuleFor(x => x)
            .Must(x => x.StalledThreshold > x.HeartbeatInterval)
            .WithMessage("StalledThreshold must exceed HeartbeatInterval so a single missed beat does not mark the worker stalled.");
    }
}

public sealed class RetryHandlingOptionsValidator : AbstractValidator<RetryHandlingOptions>
{
    public RetryHandlingOptionsValidator()
    {
        RuleFor(x => x.Default).NotNull().SetValidator(new RetryPolicyProfileValidator());
        RuleFor(x => x.SqlRead).NotNull().SetValidator(new RetryPolicyProfileValidator());
        RuleFor(x => x.SqlBlobRead).NotNull().SetValidator(new RetryPolicyProfileValidator());
        RuleFor(x => x.SqlWrite).NotNull().SetValidator(new RetryPolicyProfileValidator());
        RuleFor(x => x.DiskWrite).NotNull().SetValidator(new RetryPolicyProfileValidator());
        RuleFor(x => x.DiskRead).NotNull().SetValidator(new RetryPolicyProfileValidator());
        RuleFor(x => x.StateStore).NotNull().SetValidator(new RetryPolicyProfileValidator());
        RuleFor(x => x.Network).NotNull().SetValidator(new RetryPolicyProfileValidator());
    }
}

public sealed class RetryPolicyProfileValidator : AbstractValidator<RetryPolicyProfile>
{
    public RetryPolicyProfileValidator()
    {
        RuleFor(x => x.MaxAttempts).InclusiveBetween(1, 100);
        RuleFor(x => x.BaseDelayMilliseconds).InclusiveBetween(0, 60_000);
        RuleFor(x => x.MaxDelaySeconds).InclusiveBetween(0, 3_600);
        RuleFor(x => x.PerAttemptTimeoutSeconds).GreaterThanOrEqualTo(0);
        RuleFor(x => x.JitterFactor).InclusiveBetween(0.0, 1.0);
        RuleFor(x => x.CircuitBreaker).NotNull();
    }
}

public sealed class SqlStreamingOptionsValidator : AbstractValidator<SqlStreamingOptions>
{
    public SqlStreamingOptionsValidator()
    {
        RuleFor(x => x.FetchSize).InclusiveBetween(1, 100_000);
        RuleFor(x => x.CommandTimeoutSeconds).GreaterThan(0);
        RuleFor(x => x.BlobCommandTimeoutSeconds).GreaterThan(0);
        RuleFor(x => x.NetworkPacketSizeBytes).InclusiveBetween(512, 32_768);
        RuleFor(x => x.ProgressReportInterval).Must(t => t > TimeSpan.Zero);
        RuleFor(x => x.MaxRetryAttempts).GreaterThanOrEqualTo(0);
    }
}

public sealed class TelemetryOptionsValidator : AbstractValidator<TelemetryOptions>
{
    public TelemetryOptionsValidator()
    {
        RuleFor(x => x.ServiceName).NotEmpty();
        RuleFor(x => x.ServiceNamespace).NotEmpty();
        RuleFor(x => x.ServiceVersion).NotEmpty();
        RuleFor(x => x.TraceSamplingRatio).InclusiveBetween(0.0, 1.0);
        When(x => x.EnableOtlpExporter, () =>
        {
            RuleFor(x => x.OtlpEndpoint).NotEmpty()
                .WithMessage("OtlpEndpoint is required when EnableOtlpExporter is true.");
        });
        When(x => x.EnablePrometheusEndpoint, () =>
        {
            RuleFor(x => x.PrometheusListenerUrl).NotEmpty();
        });
    }
}

public sealed class DashboardOptionsValidator : AbstractValidator<DashboardOptions>
{
    public DashboardOptionsValidator()
    {
        RuleFor(x => x.RefreshInterval).Must(t => t >= TimeSpan.FromMilliseconds(100))
            .WithMessage("RefreshInterval must be at least 100 ms — smaller values cause flicker.");
        RuleFor(x => x.MaxWorkerRows).InclusiveBetween(1, 128);
        RuleFor(x => x.MaxDocumentKeyLength).InclusiveBetween(8, 512);
    }
}

public sealed class TrackingDatabaseOptionsValidator : AbstractValidator<TrackingDatabaseOptions>
{
    public TrackingDatabaseOptionsValidator()
    {
        RuleFor(x => x.ConnectionString).NotEmpty();
        RuleFor(x => x.CommandTimeoutSeconds).GreaterThan(0);
        RuleFor(x => x.BatchSize).InclusiveBetween(1, 100_000);
        RuleFor(x => x.MetricFlushInterval).Must(t => t > TimeSpan.Zero);
    }
}
