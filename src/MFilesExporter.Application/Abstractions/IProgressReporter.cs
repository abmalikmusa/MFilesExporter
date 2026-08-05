using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Application.Abstractions;

public interface IProgressReporter
{
    Task ReportAsync(ExportProgress progress, CancellationToken cancellationToken);
}
