using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Application.Abstractions;

public interface IManifestWriter : IAsyncDisposable
{
    Task AppendAsync(ExportOutcome outcome, CancellationToken cancellationToken);
    Task FlushAsync(CancellationToken cancellationToken);
}
