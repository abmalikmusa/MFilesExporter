using FluentValidation.TestHelper;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Configuration.Validation;

namespace MFilesExporter.Tests.Configuration;

public class ExporterOptionsValidatorTests
{
    /// <summary>
    /// Returns an <see cref="ExporterOptions"/> populated with the minimum
    /// fields required to satisfy every sub-validator. Tests mutate a single
    /// property away from this baseline to assert failure conditions.
    /// </summary>
    private static ExporterOptions Valid() => new()
    {
        Source = new MFilesSourceOptions
        {
            ConnectionString = "Server=.;Database=vault;",
            CommandTimeoutSeconds = 60,
            EnumerationBatchSize = 500,
            PartitionKey = "default",
        },
        Storage = new StorageOptions
        {
            RootPath = "./out/docs",
            ManifestPath = "./out/manifests",
            ShardDepth = 2,
            WriteBufferSize = 65_536,
            ManifestRotationEntryCount = 10_000,
        },
        Pipeline = new PipelineOptions
        {
            EnumerationChannelCapacity = 100,
            ContentChannelCapacity = 32,
            ContentReaderConcurrency = 4,
            SinkConcurrency = 4,
            OutcomeBatchSize = 50,
            ProgressReportInterval = TimeSpan.FromSeconds(1),
            CheckpointFlushInterval = TimeSpan.FromSeconds(1),
        },
        StateStore = new StateStoreOptions
        {
            Provider = "sqlite",
            ConnectionString = "./out/state.db",
        },
        TrackingDatabase = new TrackingDatabaseOptions
        {
            ConnectionString = "Server=.;Database=MFilesExportTracking;",
            CommandTimeoutSeconds = 30,
            BatchSize = 100,
        },
    };

    [Fact]
    public void Valid_Passes()
    {
        new ExporterOptionsValidator().TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyConnectionString_Fails()
    {
        var o = Valid();
        o.Source.ConnectionString = string.Empty;
        new ExporterOptionsValidator().TestValidate(o).ShouldHaveValidationErrorFor("Source.ConnectionString");
    }

    [Fact]
    public void OutOfRangeShardDepth_Fails()
    {
        var o = Valid();
        o.Storage.ShardDepth = 10;
        new ExporterOptionsValidator().TestValidate(o).ShouldHaveValidationErrorFor("Storage.ShardDepth");
    }

    [Fact]
    public void EmptyTrackingConnectionString_Fails()
    {
        var o = Valid();
        o.TrackingDatabase.ConnectionString = string.Empty;
        new ExporterOptionsValidator().TestValidate(o)
            .ShouldHaveValidationErrorFor("TrackingDatabase.ConnectionString");
    }

    [Fact]
    public void OutOfRangeBatchSize_Fails()
    {
        var o = Valid();
        o.BatchProcessing.BatchSize = 0;
        new ExporterOptionsValidator().TestValidate(o)
            .ShouldHaveValidationErrorFor("BatchProcessing.BatchSize");
    }

    [Fact]
    public void ExcessiveParallelism_Fails()
    {
        var o = Valid();
        o.BatchProcessing.MaxParallelismPerBatch = 10_000;
        new ExporterOptionsValidator().TestValidate(o)
            .ShouldHaveValidationErrorFor("BatchProcessing.MaxParallelismPerBatch");
    }

    [Fact]
    public void FailureRateOutOfRange_Fails()
    {
        var o = Valid();
        o.BatchProcessing.FailureRateThreshold = 1.5;
        new ExporterOptionsValidator().TestValidate(o)
            .ShouldHaveValidationErrorFor("BatchProcessing.FailureRateThreshold");
    }

    [Fact]
    public void RetryProfile_MaxAttemptsZero_Fails()
    {
        var o = Valid();
        o.RetryHandling.SqlRead.MaxAttempts = 0;
        new ExporterOptionsValidator().TestValidate(o)
            .ShouldHaveValidationErrorFor("RetryHandling.SqlRead.MaxAttempts");
    }

    [Fact]
    public void Telemetry_OtlpEnabledWithoutEndpoint_Fails()
    {
        var o = Valid();
        o.Telemetry.EnableOtlpExporter = true;
        o.Telemetry.OtlpEndpoint = null;
        new ExporterOptionsValidator().TestValidate(o)
            .ShouldHaveValidationErrorFor("Telemetry.OtlpEndpoint");
    }

    [Fact]
    public void Checkpoint_EmptyWalDirectory_Fails()
    {
        var o = Valid();
        o.Checkpoint.WalDirectory = string.Empty;
        new ExporterOptionsValidator().TestValidate(o)
            .ShouldHaveValidationErrorFor("Checkpoint.WalDirectory");
    }

    [Fact]
    public void FileExport_ShardDepthOutOfRange_Fails()
    {
        var o = Valid();
        o.FileExport.ShardDepth = 6;
        new ExporterOptionsValidator().TestValidate(o)
            .ShouldHaveValidationErrorFor("FileExport.ShardDepth");
    }

    [Fact]
    public void Metadata_EmptyOutputDirectory_Fails()
    {
        var o = Valid();
        o.Metadata.OutputDirectory = string.Empty;
        new ExporterOptionsValidator().TestValidate(o)
            .ShouldHaveValidationErrorFor("Metadata.OutputDirectory");
    }

    [Fact]
    public void SqlStreaming_FetchSizeZero_Fails()
    {
        var o = Valid();
        o.SqlStreaming.FetchSize = 0;
        new ExporterOptionsValidator().TestValidate(o)
            .ShouldHaveValidationErrorFor("SqlStreaming.FetchSize");
    }
}
