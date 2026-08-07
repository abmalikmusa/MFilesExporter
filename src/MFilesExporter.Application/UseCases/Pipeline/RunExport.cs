using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using MFilesExporter.Application.Models.Tracking;
using MFilesExporter.Application.UseCases.Jobs;
using MFilesExporter.Application.UseCases.Workers;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Application.UseCases.Pipeline;

/// <summary>
/// Top-level orchestration command. Composes the full lifecycle of one
/// export run:
/// <list type="number">
///   <item><description>Start the job (StartExportJob).</description></item>
///   <item><description>Register the local worker (RegisterWorker).</description></item>
///   <item><description>Run the streaming pipeline (IExportPipeline).</description></item>
///   <item><description>Stop the worker (StopWorker).</description></item>
///   <item><description>Complete the job (CompleteExportJob).</description></item>
/// </list>
///
/// Failures at any stage transition the job into Failed and stop the worker.
/// </summary>
public sealed record RunExportCommand : ICommand<RunExportSummary>
{
    public required string JobName { get; init; }
    public required string SourceServer { get; init; }
    public required string SourceDatabase { get; init; }
    public required string PartitionKey { get; init; }
    public required string WorkerName { get; init; }
    public required string MachineName { get; init; }
    public required int Concurrency { get; init; }
    public long? TotalDocumentsExpected { get; init; }
    public int? ProcessId { get; init; }
}

public sealed record RunExportSummary(long ExportJobId, long ExportWorkerId, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc);

public sealed class RunExportHandler : ICommandHandler<RunExportCommand, RunExportSummary>
{
    private readonly IApplicationDispatcher _dispatcher;
    private readonly IExportPipeline _pipeline;
    private readonly IJobContext _jobContext;
    private readonly IClock _clock;
    private readonly ILogger<RunExportHandler> _logger;

    public RunExportHandler(
        IApplicationDispatcher dispatcher,
        IExportPipeline pipeline,
        IJobContext jobContext,
        IClock clock,
        ILogger<RunExportHandler> logger)
    {
        _dispatcher = dispatcher;
        _pipeline = pipeline;
        _jobContext = jobContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ApplicationResult<RunExportSummary>> HandleAsync(
        RunExportCommand command,
        CancellationToken cancellationToken)
    {
        var startedAt = _clock.UtcNow;
        long jobId = 0;
        long workerId = 0;
        Exception? failure = null;

        try
        {
            var jobResult = await _dispatcher.SendAsync<StartExportJobCommand, long>(
                new StartExportJobCommand
                {
                    JobName = command.JobName,
                    SourceServer = command.SourceServer,
                    SourceDatabase = command.SourceDatabase,
                    PartitionKey = command.PartitionKey,
                    TotalDocumentsExpected = command.TotalDocumentsExpected,
                },
                cancellationToken).ConfigureAwait(false);

            if (jobResult.IsFailure)
            {
                return ApplicationResult<RunExportSummary>.Failure(jobResult.Errors);
            }
            jobId = jobResult.Value;
            _jobContext.SetCurrent(jobId);   // populate ambient scope for CheckpointEngine et al.

            var workerResult = await _dispatcher.SendAsync<RegisterWorkerCommand, long>(
                new RegisterWorkerCommand
                {
                    ExportJobId = jobId,
                    WorkerName = command.WorkerName,
                    MachineName = command.MachineName,
                    ProcessId = command.ProcessId,
                    AssignedPartition = command.PartitionKey,
                    Concurrency = command.Concurrency,
                },
                cancellationToken).ConfigureAwait(false);

            if (workerResult.IsFailure)
            {
                await FailJobAsync(jobId, "worker registration failed", cancellationToken).ConfigureAwait(false);
                return ApplicationResult<RunExportSummary>.Failure(workerResult.Errors);
            }
            workerId = workerResult.Value;

            await _pipeline.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = new OperationCanceledException("Run cancelled.");
        }
        catch (Exception ex)
        {
            failure = ex;
            _logger.LogError(ex, "Export run faulted during pipeline execution");
        }

        // Cleanup: stop worker + complete job (always).
        if (workerId > 0)
        {
            await _dispatcher.SendAsync(new StopWorkerCommand
            {
                ExportWorkerId = workerId,
                Reason = failure?.Message,
            }, CancellationToken.None).ConfigureAwait(false);
        }

        if (jobId > 0)
        {
            var terminal =
                failure is OperationCanceledException ? ExportJobStatus.Cancelled :
                failure is null                       ? ExportJobStatus.Completed :
                                                        ExportJobStatus.Failed;

            await _dispatcher.SendAsync(new CompleteExportJobCommand
            {
                ExportJobId = jobId,
                TerminalStatus = terminal,
                Reason = failure?.Message,
            }, CancellationToken.None).ConfigureAwait(false);
        }

        // Ambient scope is drained regardless of outcome.
        _jobContext.Clear();

        if (failure is not null)
        {
            return ApplicationResult<RunExportSummary>.Failure(
                failure is OperationCanceledException
                    ? ApplicationError.Conflict("RUN_CANCELLED", failure.Message)
                    : ApplicationError.Unexpected("RUN_FAILED", failure.Message));
        }

        return ApplicationResult<RunExportSummary>.Success(
            new RunExportSummary(jobId, workerId, startedAt, _clock.UtcNow));
    }

    private Task FailJobAsync(long jobId, string reason, CancellationToken ct) =>
        _dispatcher.SendAsync(new CompleteExportJobCommand
        {
            ExportJobId = jobId,
            TerminalStatus = ExportJobStatus.Failed,
            Reason = reason,
        }, ct);
}
