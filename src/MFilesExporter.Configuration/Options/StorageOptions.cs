namespace MFilesExporter.Configuration.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Exporter:Storage";

    public string RootPath { get; set; } = "./export-output/documents";
    public string ManifestPath { get; set; } = "./export-output/manifests";
    public int ShardDepth { get; set; } = 2;
    public int WriteBufferSize { get; set; } = 81_920;
    public int ManifestRotationEntryCount { get; set; } = 100_000;
    public bool FsyncManifestOnRotate { get; set; } = true;
    public bool PreserveOriginalFilename { get; set; } = true;
    public int MinimumFreeSpaceGb { get; set; } = 50;
}
