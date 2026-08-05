namespace MFilesExporter.Configuration.Options;

/// <summary>
/// Configuration for the metadata generation framework — the layer that
/// emits <c>metadata.csv</c>, <c>metadata.json</c>, and
/// <c>manifest.json</c> alongside the exported document files.
/// </summary>
public sealed class MetadataOptions
{
    public const string SectionName = "Exporter:Metadata";

    /// <summary>Directory into which metadata artifacts are written.</summary>
    public string OutputDirectory { get; set; } = "./export-output/metadata";

    /// <summary>Emit <c>metadata.csv</c>.</summary>
    public bool WriteCsv { get; set; } = true;

    /// <summary>Emit <c>metadata.json</c> (well-formed JSON array).</summary>
    public bool WriteJson { get; set; } = true;

    /// <summary>Emit <c>manifest.json</c> at the end of the run.</summary>
    public bool WriteManifest { get; set; } = true;

    /// <summary>Filename for the CSV artifact.</summary>
    public string CsvFileName { get; set; } = "metadata.csv";

    /// <summary>Filename for the JSON artifact.</summary>
    public string JsonFileName { get; set; } = "metadata.json";

    /// <summary>Filename for the run-level manifest.</summary>
    public string ManifestFileName { get; set; } = "manifest.json";

    /// <summary>CSV field delimiter. Comma by default; set to <c>\t</c> for TSV.</summary>
    public string CsvDelimiter { get; set; } = ",";

    /// <summary>
    /// Prepend a UTF-8 byte-order mark to the CSV so Microsoft Excel
    /// on Windows opens Unicode content correctly.
    /// </summary>
    public bool CsvIncludeUtf8Bom { get; set; } = true;

    /// <summary>Include the header row in the CSV.</summary>
    public bool CsvIncludeHeader { get; set; } = true;

    /// <summary>
    /// Indent JSON output for human readability. Disable for large exports
    /// (unindented JSON is ~30 % smaller and much faster to write).
    /// </summary>
    public bool JsonIndent { get; set; }

    /// <summary>
    /// Extension attributes to include in JSON records:
    /// <c>IdempotencyKey</c> and <c>DataFileVersionId</c>. Enabled by
    /// default because EDMS migration tools benefit from having both the
    /// natural key and the source system's surrogate key.
    /// </summary>
    public bool IncludeExtensionFields { get; set; } = true;

    /// <summary>Flush artifact writers after every N records. Balances crash-safety and syscall cost.</summary>
    public int FlushEveryNRecords { get; set; } = 500;
}
