using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;

namespace MFilesExporter.Application.UseCases.Progress;

/// <summary>
/// Monotonic checkpoint upsert. Returns whether the checkpoint advanced —
/// a candidate less than the current active cursor produces
/// <c>Success(false)</c>, not a failure.
/// </summary>
public sealed record SaveCheckpointCommand : ICommand<bool>
{
    public required long ExportJobId { get; init; }
    public required string PartitionKey { get; init; }
    public required long LastDocumentFilePartId { get; init; }
    public required long LastVersionPartId { get; init; }
    public long? DocumentsProcessedInPartition { get; init; }
}

public sealed class SaveCheckpointHandler : ICommandHandler<SaveCheckpointCommand, bool>
{
    private readonly IExportCheckpointRepository _checkpoints;

    public SaveCheckpointHandler(IExportCheckpointRepository checkpoints)
    {
        _checkpoints = checkpoints;
    }

    public async Task<ApplicationResult<bool>> HandleAsync(
        SaveCheckpointCommand command,
        CancellationToken cancellationToken)
    {
        var errors = new List<ApplicationError>();
        if (command.ExportJobId <= 0)
            errors.Add(ApplicationError.Validation("JOB_ID_REQUIRED", "ExportJobId must be positive."));
        if (string.IsNullOrWhiteSpace(command.PartitionKey))
            errors.Add(ApplicationError.Validation("PARTITION_REQUIRED", "PartitionKey is required."));

        if (errors.Count > 0) return ApplicationResult<bool>.Failure(errors);

        try
        {
            var advanced = await _checkpoints.SaveAsync(
                command.ExportJobId,
                command.PartitionKey,
                command.LastDocumentFilePartId,
                command.LastVersionPartId,
                command.DocumentsProcessedInPartition,
                cancellationToken).ConfigureAwait(false);
            return ApplicationResult<bool>.Success(advanced);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApplicationResult<bool>.Failure(
                ApplicationError.Transient("CHECKPOINT_SAVE_FAILED", ex.Message));
        }
    }
}
