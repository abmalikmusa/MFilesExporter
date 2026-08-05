namespace MFilesExporter.Domain.Batches;

/// <summary>Lifecycle state of an <see cref="ExportBatch"/>.</summary>
public enum BatchStatus
{
    Created  = 0,
    Enumerated = 1,
    Processing = 2,
    Completed = 3,
    Failed = 4,
}
