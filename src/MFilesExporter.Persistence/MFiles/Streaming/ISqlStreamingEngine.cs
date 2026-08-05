using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Persistence.MFiles.Streaming;

/// <summary>
/// The SQL streaming engine — the single component that executes the
/// canonical M-Files document query in a memory-bounded, resumable,
/// cancellable, retryable fashion.
/// </summary>
/// <remarks>
/// The canonical query returns metadata joined with BLOB payload. The
/// engine derives that result set as (a) a keyset-paginated metadata
/// enumeration and (b) a per-document BLOB stream opened on demand. See
/// <c>docs/mfiles-schema.md</c> for the equivalence argument.
/// </remarks>
public interface ISqlStreamingEngine
{
    /// <summary>
    /// Streams committed document descriptors greater than
    /// <paramref name="exclusiveLowerBound"/>, in ascending
    /// <c>(ID_DOCUMENTFILEPART, ID_VERSIONPART)</c> order.
    /// </summary>
    /// <param name="exclusiveLowerBound">Resume cursor. Use
    /// <see cref="DocumentFileVersionKey.Origin"/> to start from the beginning.</param>
    /// <param name="runOptions">Per-invocation overrides.</param>
    /// <param name="progress">Optional progress sink. Called on the configured interval.</param>
    /// <param name="cancellationToken">Cancels the stream; already-yielded descriptors remain valid.</param>
    IAsyncEnumerable<StreamedDocumentDescriptor> StreamAsync(
        DocumentFileVersionKey exclusiveLowerBound,
        SqlStreamingRunOptions? runOptions,
        IProgress<SqlStreamingProgress>? progress,
        CancellationToken cancellationToken);
}
