namespace MFilesExporter.Domain.Documents;

public enum ExportStatus
{
    Unknown = 0,
    Pending = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4,
}
