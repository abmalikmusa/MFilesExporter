using MFilesExporter.Application.Abstractions;
using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Export.Pipeline;

public sealed record PreparedDocument(
    DocumentDescriptor Descriptor,
    DocumentContentStream ContentStream,
    int AttemptNumber);
