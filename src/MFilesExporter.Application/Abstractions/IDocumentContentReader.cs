using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Application.Abstractions;

public interface IDocumentContentReader
{
    Task<DocumentContentStream> OpenAsync(DataFileVersionKey key, CancellationToken cancellationToken);
}

public sealed class DocumentContentStream : IAsyncDisposable
{
    private readonly Func<ValueTask> _dispose;

    public DocumentContentStream(Stream content, long length, Func<ValueTask> dispose)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Length = length;
        _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
    }

    public Stream Content { get; }
    public long Length { get; }

    public ValueTask DisposeAsync() => _dispose();
}
