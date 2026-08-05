using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Domain.Exceptions;

/// <summary>
/// Raised when DOCUMENTFILEVERSION references a DATAFILEVERSION whose BLOB row
/// is absent from DATAFILEVERSION_BYTES or has UPLOADCOMMITTED != 1.
/// Treated as deterministically un-exportable (Skipped), not as transient failure.
/// </summary>
public sealed class DocumentContentMissingException : DomainException
{
    public DocumentContentMissingException(DataFileVersionKey key)
        : base($"No committed BLOB found for {key}.")
    {
        Key = key;
    }

    public DataFileVersionKey Key { get; }
}
