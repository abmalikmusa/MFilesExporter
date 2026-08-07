using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Batching;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.UseCases.Errors;
using MFilesExporter.Application.UseCases.Jobs;
using MFilesExporter.Application.UseCases.Pipeline;
using MFilesExporter.Application.UseCases.Progress;
using MFilesExporter.Application.UseCases.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace MFilesExporter.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddExporterApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Ambient job context — populated by RunExportHandler; consumed by
        // CheckpointEngine + any component that needs to attribute writes
        // to a real ExportJobId.
        services.AddSingleton<IJobContext, JobContext>();

        // Dispatcher — inner concrete + logging decorator surfaced as the interface.
        services.AddSingleton<ApplicationDispatcher>();
        services.AddSingleton<IApplicationDispatcher>(sp => new LoggingApplicationDispatcher(
            sp.GetRequiredService<ApplicationDispatcher>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LoggingApplicationDispatcher>>()));

        // Command handlers — Jobs.
        services.AddSingleton<ICommandHandler<StartExportJobCommand, long>, StartExportJobHandler>();
        services.AddSingleton<ICommandHandler<CompleteExportJobCommand>, CompleteExportJobHandler>();
        services.AddSingleton<ICommandHandler<CancelExportJobCommand>, CancelExportJobHandler>();

        // Query handlers — Jobs.
        services.AddSingleton<IQueryHandler<GetJobStatusQuery, MFilesExporter.Application.Models.Tracking.ExportJobRecord>, GetJobStatusHandler>();
        services.AddSingleton<IQueryHandler<GetJobStatisticsQuery, JobStatisticsView>, GetJobStatisticsHandler>();

        // Command handlers — Workers.
        services.AddSingleton<ICommandHandler<RegisterWorkerCommand, long>, RegisterWorkerHandler>();
        services.AddSingleton<ICommandHandler<HeartbeatWorkerCommand>, HeartbeatWorkerHandler>();
        services.AddSingleton<ICommandHandler<StopWorkerCommand>, StopWorkerHandler>();

        // Command handlers — Progress.
        services.AddSingleton<ICommandHandler<SaveCheckpointCommand, bool>, SaveCheckpointHandler>();
        services.AddSingleton<ICommandHandler<RecordProgressSnapshotCommand>, RecordProgressSnapshotHandler>();
        services.AddSingleton<IQueryHandler<GetLatestProgressQuery, MFilesExporter.Application.Models.Tracking.ExportProgressRecord>, GetLatestProgressHandler>();
        services.AddSingleton<IQueryHandler<GetActiveCheckpointQuery, MFilesExporter.Application.Models.Tracking.ExportCheckpointRecord>, GetActiveCheckpointHandler>();

        // Command handlers — Errors.
        services.AddSingleton<ICommandHandler<LogErrorCommand, long>, LogErrorHandler>();
        services.AddSingleton<ICommandHandler<ResolveErrorCommand>, ResolveErrorHandler>();
        services.AddSingleton<IQueryHandler<GetRecentAuditQuery, IReadOnlyList<MFilesExporter.Application.Models.Tracking.ExportAuditRecord>>, GetRecentAuditHandler>();

        // Top-level pipeline orchestration.
        services.AddSingleton<ICommandHandler<RunExportCommand, RunExportSummary>, RunExportHandler>();

        // Batch processing engine.
        services.AddSingleton<IBatchExecutor, ParallelBatchExecutor>();
        services.AddSingleton<IBatchCoordinator, SequentialBatchCoordinator>();
        services.AddSingleton<IBatchSource<Domain.WorkClaiming.ClaimedWorkItem>, WorkClaimBatchSource>();

        return services;
    }
}
