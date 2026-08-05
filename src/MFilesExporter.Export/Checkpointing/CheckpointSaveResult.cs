namespace MFilesExporter.Export.Checkpointing;

/// <summary>
/// Outcome of one <see cref="ICheckpointEngine.SaveAsync"/> call. The
/// <c>Advanced</c> flag reflects whether ANY layer moved forward; the
/// per-layer booleans surface partial failures so callers can log
/// divergence without failing the batch.
/// </summary>
public sealed record CheckpointSaveResult
{
    public required bool Advanced { get; init; }
    public required bool WalWritten { get; init; }
    public required bool SqlWritten { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public string? Warning { get; init; }
}
