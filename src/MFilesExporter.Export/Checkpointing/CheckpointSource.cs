namespace MFilesExporter.Export.Checkpointing;

/// <summary>Where the recovered checkpoint value came from.</summary>
public enum CheckpointSource
{
    /// <summary>No checkpoint found — start at <c>DocumentFileVersionKey.Origin</c>.</summary>
    Origin = 0,

    /// <summary>Read from the local Write-Ahead Log.</summary>
    Wal = 1,

    /// <summary>Read from the SQL Server tracking DB.</summary>
    SqlServer = 2,

    /// <summary>Both sources agreed.</summary>
    WalAndSql = 3,
}
