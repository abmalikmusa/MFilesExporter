using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Application.Abstractions;

public interface IDocumentSink
{
    Task<DocumentSinkResult> WriteAsync(
        DocumentDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken);
}

public sealed record DocumentSinkResult(string OutputPath, long BytesWritten, string ChecksumHex);
