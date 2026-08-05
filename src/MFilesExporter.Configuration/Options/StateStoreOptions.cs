namespace MFilesExporter.Configuration.Options;

public sealed class StateStoreOptions
{
    public const string SectionName = "Exporter:StateStore";

    public string Provider { get; set; } = "sqlite";
    public string ConnectionString { get; set; } = "./export-output/state.db";
    public bool EnableMemoryMappedIo { get; set; } = true;
    public int CacheSizeKib { get; set; } = 65_536;
    public TimeSpan WalCheckpointInterval { get; set; } = TimeSpan.FromMinutes(5);
}
