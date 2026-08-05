namespace MFilesExporter.Export.Checkpointing;

/// <summary>
/// Persists enumeration progress durably at every batch boundary and
/// reconstructs it on start-up. Combines a local Write-Ahead Log (survives
/// power/OS/app crash) with a SQL Server checkpoint row (cross-node
/// durability + audit).
/// </summary>
public interface ICheckpointEngine
{
    /// <summary>
    /// Reads the persisted checkpoint from every configured layer and
    /// returns the highest value. Returns a fresh <c>Origin</c> when nothing
    /// has been persisted yet.
    /// </summary>
    Task<CheckpointState> RecoverAsync(
        long jobId,
        string partitionKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists <paramref name="candidate"/> to every configured layer.
    /// Called by the batch coordinator after each batch completes.
    /// </summary>
    Task<CheckpointSaveResult> SaveAsync(
        long jobId,
        string partitionKey,
        CheckpointCandidate candidate,
        CancellationToken cancellationToken);
}
