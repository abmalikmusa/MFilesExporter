using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Export.Files;
using MFilesExporter.Export.Files.Naming;
using MFilesExporter.Export.Files.Strategies;
using MFilesExporter.Export.Manifest;
using MFilesExporter.Export.Metadata;
using MFilesExporter.Export.Pipeline;
using MFilesExporter.Export.Storage;
using MFilesExporter.Export.Checkpointing;
using MFilesExporter.Export.Checkpointing.WriteAheadLog;
using MFilesExporter.Export.Validation;
using MFilesExporter.Export.Validation.Reporting;
using MFilesExporter.Export.Validation.Validators;
using Microsoft.Extensions.DependencyInjection;

namespace MFilesExporter.Export.DependencyInjection;

public static class ExportServiceCollectionExtensions
{
    public static IServiceCollection AddExporterExport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IChecksumCalculatorFactory, Sha256ChecksumCalculatorFactory>();
        services.AddSingleton<PathBuilder>();
        services.AddSingleton<IDocumentSink, FileSystemDocumentSink>();
        services.AddSingleton<IManifestWriter, JsonLinesManifestWriter>();

        services.AddSingleton<PipelineChannels>();
        services.AddSingleton<ProducerStage>();
        services.AddSingleton<ContentReaderStage>();
        services.AddSingleton<SinkStage>();
        services.AddSingleton<OutcomeCollectorStage>();
        services.AddSingleton<IExportPipeline, ExportPipeline>();

        // File Export Engine — original-filename path with pluggable strategy.
        services.AddSingleton<IFilenameSanitizer, FilenameSanitizer>();
        services.AddSingleton<IFolderStrategy>(sp =>
            FolderStrategyFactory.Create(sp.GetRequiredService<FileExportOptions>()));
        services.AddSingleton<IDuplicateResolver>(sp =>
            DuplicateResolverFactory.Create(sp.GetRequiredService<FileExportOptions>()));
        services.AddSingleton<IFileExportEngine, FileExportEngine>();

        // Metadata generation framework — CSV, JSON, and manifest artifacts.
        services.AddSingleton<ManifestJsonWriter>();
        services.AddSingleton<IMetadataGenerator, MetadataGenerator>();

        // Post-export validation pipeline: seven bundled validators + logging reporter.
        services.AddSingleton<IExportValidator, FileExistsValidator>();
        services.AddSingleton<IExportValidator, OutputFolderValidator>();
        services.AddSingleton<IExportValidator, ExtensionValidator>();
        services.AddSingleton<IExportValidator, FileSizeValidator>();
        services.AddSingleton<IExportValidator, ReadableValidator>();
        services.AddSingleton<IExportValidator, ChecksumValidator>();
        services.AddSingleton<IExportValidator, MetadataConsistencyValidator>();
        services.AddSingleton<IValidationReporter, LoggingValidationReporter>();
        services.AddSingleton<IExportValidationPipeline, ExportValidationPipeline>();

        // Checkpoint engine — WAL + SQL layered durability.
        services.AddSingleton<ICheckpointWal, FileCheckpointWal>();
        services.AddSingleton<ICheckpointEngine, CheckpointEngine>();

        return services;
    }
}
