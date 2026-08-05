namespace MFilesExporter.Export.Checkpointing.WriteAheadLog;

/// <summary>
/// Local, single-slot durable log used to persist the enumeration cursor
/// after every batch. Writes are atomic (temp-file + rename) and fsync'd
/// so a crash-restart never observes a partially-written WAL.
/// </summary>
public interface ICheckpointWal
{
    /// <summary>Writes the latest checkpoint. Overwrites the single slot atomically.</summary>
    Task AppendAsync(long jobId, string partitionKey, WalEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the most recent valid WAL entry, or <c>null</c> when either the
    /// file is missing or its CRC does not match (a partial write).
    /// </summary>
    Task<WalEntry?> ReadLatestAsync(long jobId, string partitionKey, CancellationToken cancellationToken);
}
