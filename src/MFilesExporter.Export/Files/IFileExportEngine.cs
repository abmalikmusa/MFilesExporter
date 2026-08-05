namespace MFilesExporter.Export.Files;

/// <summary>
/// Writes a document's binary payload to the filesystem, using its
/// original TITLE + EXTENSION under the configured folder strategy.
///
/// The engine composes:
/// <list type="bullet">
///   <item><description><c>IFolderStrategy</c> — where the file lives (relative folder).</description></item>
///   <item><description><c>IFilenameSanitizer</c> — safe filename derivation from TITLE + EXTENSION.</description></item>
///   <item><description><c>IDuplicateResolver</c> — collision handling.</description></item>
/// </list>
/// </summary>
public interface IFileExportEngine
{
    /// <summary>Writes the payload and returns the final path plus diagnostics.</summary>
    /// <param name="context">Document identity + optional category.</param>
    /// <param name="content">Streaming source of bytes. Consumed until EOF.</param>
    /// <param name="cancellationToken">Cancels the write; partial files are removed.</param>
    Task<FileExportResult> ExportAsync(
        FileExportContext context,
        Stream content,
        CancellationToken cancellationToken);
}
