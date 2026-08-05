using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Export.Checkpointing;

/// <summary>Recovered checkpoint value returned to callers on start-up.</summary>
public sealed record CheckpointState(
    DocumentFileVersionKey Cursor,
    long DocumentsProcessedInPartition,
    DateTimeOffset PersistedAtUtc,
    CheckpointSource Source)
{
    /// <summary>Sentinel returned when no persisted checkpoint exists.</summary>
    public static CheckpointState AtOrigin(DateTimeOffset now) =>
        new(DocumentFileVersionKey.Origin, 0, now, CheckpointSource.Origin);
}
