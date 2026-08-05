namespace MFilesExporter.Domain.Documents;

/// <summary>
/// Composite primary key of DOCUMENTFILEVERSION: (ID_DOCUMENTFILEPART, ID_VERSIONPART).
/// Serves as the keyset-pagination cursor for resumable enumeration.
/// </summary>
public readonly record struct DocumentFileVersionKey(long DocumentFilePartId, long VersionPartId)
    : IComparable<DocumentFileVersionKey>
{
    public static DocumentFileVersionKey Origin { get; } = new(long.MinValue, long.MinValue);

    public int CompareTo(DocumentFileVersionKey other)
    {
        var partCmp = DocumentFilePartId.CompareTo(other.DocumentFilePartId);
        return partCmp != 0 ? partCmp : VersionPartId.CompareTo(other.VersionPartId);
    }

    public static bool operator <(DocumentFileVersionKey l, DocumentFileVersionKey r) => l.CompareTo(r) < 0;
    public static bool operator >(DocumentFileVersionKey l, DocumentFileVersionKey r) => l.CompareTo(r) > 0;
    public static bool operator <=(DocumentFileVersionKey l, DocumentFileVersionKey r) => l.CompareTo(r) <= 0;
    public static bool operator >=(DocumentFileVersionKey l, DocumentFileVersionKey r) => l.CompareTo(r) >= 0;

    public override string ToString() => $"{DocumentFilePartId}:{VersionPartId}";
}
