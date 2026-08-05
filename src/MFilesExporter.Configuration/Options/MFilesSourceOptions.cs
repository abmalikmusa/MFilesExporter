namespace MFilesExporter.Configuration.Options;

public sealed class MFilesSourceOptions
{
    public const string SectionName = "Exporter:Source";

    public string ConnectionString { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 120;
    public int EnumerationBatchSize { get; set; } = 1_000;
    public bool UseReadUncommittedForEnumeration { get; set; } = true;
    public string PartitionKey { get; set; } = "default";
    public MFilesTables Tables { get; set; } = new();
}

public sealed class MFilesTables
{
    public string DocumentFileVersion { get; set; } = "DOCUMENTFILEVERSION";
    public string DataFileVersion { get; set; } = "DATAFILEVERSION";
    public string DataFileVersionBytes { get; set; } = "DATAFILEVERSION_BYTES";
}
