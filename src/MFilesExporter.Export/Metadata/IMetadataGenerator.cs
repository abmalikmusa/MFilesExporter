namespace MFilesExporter.Export.Metadata;

/// <summary>
/// Facade over every enabled <see cref="IMetadataWriter"/>. Callers append
/// records once; the generator fans out to CSV / JSON / any future format.
/// The lifecycle is Initialize → Append × N → FinalizeAndWriteManifest.
/// </summary>
public interface IMetadataGenerator
{
    /// <summary>Opens every enabled writer.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Appends one record to every enabled writer.</summary>
    Task AppendAsync(MetadataRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Closes every writer, emits <c>manifest.json</c>, and returns the
    /// artifact references (paths + per-artifact record counts).
    /// </summary>
    Task<IReadOnlyList<ManifestArtifactReference>> FinalizeAsync(
        ManifestSummary summaryWithoutArtifacts,
        CancellationToken cancellationToken);
}
