namespace MFilesExporter.Configuration.Options;

/// <summary>
/// Configuration for the SQL streaming engine — the module that executes
/// the canonical M-Files query in a memory-bounded fashion using
/// <c>SqlDataReader</c> under <c>CommandBehavior.SequentialAccess</c> and
/// <c>SqlDataReader.GetBytes(...)</c> for BLOBs.
/// </summary>
/// <remarks>
/// Distinct from <see cref="MFilesSourceOptions"/> so timing/paging knobs
/// can be tuned per-engine without touching the connection string.
/// </remarks>
public sealed class SqlStreamingOptions
{
    public const string SectionName = "Exporter:SqlStreaming";

    /// <summary>
    /// Rows fetched per keyset-paginated round-trip. This is the "fetch size"
    /// requirement — bounded so the engine never asks SQL Server for a
    /// larger contiguous result than we can process in one page.
    /// </summary>
    public int FetchSize { get; set; } = 1_000;

    /// <summary>Command timeout, in seconds, applied to every <c>SqlCommand</c>.</summary>
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Extra command timeout applied specifically to BLOB reads. BLOB
    /// streams for very large payloads can exceed the metadata timeout.
    /// </summary>
    public int BlobCommandTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// TDS network packet size, in bytes, appended to the connection
    /// string (<c>Packet Size=</c>). Larger = fewer round-trips per
    /// large BLOB; SQL Server max is 32 768.
    /// </summary>
    public int NetworkPacketSizeBytes { get; set; } = 8_192;

    /// <summary>
    /// Read <c>DOCUMENTFILEVERSION</c> / <c>DATAFILEVERSION</c> at
    /// READ UNCOMMITTED to avoid blocking M-Files sessions. BLOB reads
    /// always run at READ COMMITTED regardless of this flag.
    /// </summary>
    public bool UseReadUncommittedForEnumeration { get; set; } = true;

    /// <summary>Interval at which the engine emits an <c>IProgress</c> tick.</summary>
    public TimeSpan ProgressReportInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Max retry attempts for a single SQL operation (metadata or BLOB open).</summary>
    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>Base delay for exponential backoff between retries.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Ceiling for the exponential backoff.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);
}
