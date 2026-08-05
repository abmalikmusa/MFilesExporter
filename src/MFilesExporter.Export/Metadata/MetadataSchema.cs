namespace MFilesExporter.Export.Metadata;

/// <summary>
/// Versioned schema descriptor embedded in every artifact
/// (<c>metadata.csv</c> comment header, <c>metadata.json</c> envelope,
/// <c>manifest.json</c> root). Downstream EDMS migration tooling reads
/// this before parsing so it knows which field set to expect.
/// </summary>
/// <remarks>
/// Follow semantic-versioning conventions:
/// <list type="bullet">
///   <item><description>Bump MAJOR on breaking changes (renamed / removed fields).</description></item>
///   <item><description>Bump MINOR on additive changes (new optional fields).</description></item>
///   <item><description>Bump PATCH on documentation / clarification only.</description></item>
/// </list>
/// </remarks>
public static class MetadataSchema
{
    /// <summary>Current schema version. Change together with a docs update.</summary>
    public const string Version = "1.0.0";

    /// <summary>Stable identifier for downstream tools.</summary>
    public const string SchemaId = "seamfix.mfiles-exporter.metadata/1.0";

    /// <summary>Producer identifier written into every artifact.</summary>
    public const string GeneratorName = "MFilesExporter";
}
