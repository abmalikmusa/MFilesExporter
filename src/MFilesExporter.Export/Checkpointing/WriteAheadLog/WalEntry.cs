using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Export.Checkpointing.WriteAheadLog;

/// <summary>One record written to (or read from) the checkpoint WAL.</summary>
public sealed record WalEntry(
    DocumentFileVersionKey Cursor,
    long DocumentsProcessedInPartition,
    DateTimeOffset PersistedAtUtc);
