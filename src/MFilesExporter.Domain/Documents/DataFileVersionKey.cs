namespace MFilesExporter.Domain.Documents;

/// <summary>
/// Composite primary key of DATAFILEVERSION and DATAFILEVERSION_BYTES:
/// (ID_DOCUMENTFILEPART, ID_DATAFILEVERSION). Used for the point-lookup
/// BLOB fetch.
/// </summary>
public readonly record struct DataFileVersionKey(long DocumentFilePartId, long DataFileVersionId)
{
    public override string ToString() => $"{DocumentFilePartId}:{DataFileVersionId}";
}
