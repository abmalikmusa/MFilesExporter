namespace MFilesExporter.Configuration.Options;

/// <summary>
/// Configuration for the checkpoint engine — the component that persists
/// enumeration progress after every batch so a crashed exporter resumes
/// from the last known position.
/// </summary>
public sealed class CheckpointOptions
{
    public const string SectionName = "Exporter:Checkpoint";

    /// <summary>Directory containing the Write-Ahead Log files.</summary>
    public string WalDirectory { get; set; } = "./export-output/checkpoints";

    /// <summary>
    /// Force fsync (flush-to-disk) after every WAL write. Set to false only
    /// on ephemeral test hardware — disabling loses the power-outage safety
    /// property.
    /// </summary>
    public bool FsyncOnWrite { get; set; } = true;

    /// <summary>
    /// Also persist to the SQL Server tracking DB. Off only in single-node
    /// deployments where the tracking DB is not provisioned.
    /// </summary>
    public bool PersistToTrackingDb { get; set; } = true;

    /// <summary>
    /// Timeout for a single SQL save. Longer than the default resilience
    /// timeout because a checkpoint save is critical and must not race the
    /// batch's own timeout.
    /// </summary>
    public TimeSpan SqlSaveTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// When the WAL and SQL disagree on recovery (WAL &gt; SQL), catch up the
    /// SQL side by re-saving the WAL's value. Off means the divergence is
    /// only surfaced via logs.
    /// </summary>
    public bool ReconcileSqlOnRecovery { get; set; } = true;
}
