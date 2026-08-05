namespace MFilesExporter.Export.Metadata;

/// <summary>
/// Streaming, append-only writer for the per-document metadata catalog.
/// Implementations MUST NOT buffer all records in memory — each
/// <see cref="AppendAsync"/> call writes immediately, releasing the
/// record for GC.
/// </summary>
public interface IMetadataWriter : IAsyncDisposable
{
    /// <summary>Human-readable format label (<c>csv</c>, <c>json</c>).</summary>
    string Format { get; }

    /// <summary>Absolute path of the file being written.</summary>
    string OutputPath { get; }

    /// <summary>Cumulative record count written so far.</summary>
    long RecordCount { get; }

    /// <summary>Opens the file and writes any preamble (BOM, header row, JSON array open).</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Appends one record. Thread-safe: multiple workers may call concurrently.</summary>
    Task AppendAsync(MetadataRecord record, CancellationToken cancellationToken);

    /// <summary>Writes any trailer (JSON array close, final flush), then closes the file.</summary>
    Task FinalizeAsync(CancellationToken cancellationToken);
}
