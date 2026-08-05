using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Export.Files;

/// <summary>
/// Ambient context supplied by the caller for a single export. Immutable —
/// safe to share across parallel exports.
/// </summary>
public sealed record FileExportContext
{
    /// <summary>The document being exported. Provides TITLE, EXTENSION, IdempotencyKey, timestamps.</summary>
    public required DocumentDescriptor Descriptor { get; init; }

    /// <summary>
    /// Optional category label used by <c>Category</c> folder strategy.
    /// When null, falls back to <see cref="DocumentMetadata.Extension"/>.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>Correlation identifier for tracing this export end-to-end.</summary>
    public string? CorrelationId { get; init; }
}
