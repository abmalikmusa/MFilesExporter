using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Export.Checkpointing;

/// <summary>Producer-side proposal for the next checkpoint value.</summary>
public sealed record CheckpointCandidate(
    DocumentFileVersionKey Cursor,
    long DocumentsProcessedInPartition);
