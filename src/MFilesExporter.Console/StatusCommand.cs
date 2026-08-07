using MFilesExporter.Configuration.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace MFilesExporter.Console;

/// <summary>
/// Handles the <c>--status</c> command-line switch.
/// Connects to the tracking database, runs a small set of read-only queries,
/// and prints a formatted text summary. Does not build the host — the intent
/// is to be safely runnable on a machine where the service is already active.
/// </summary>
/// <remarks>
/// The command is a **read-only** operator surface. It never mutates data
/// and never blocks on writes; queries take &lt; 1 second against a tracking
/// DB with a running job.
/// </remarks>
public static class StatusCommand
{
    public const string FlagName = "--status";

    /// <summary>Returns true if <paramref name="args"/> contains the status flag.</summary>
    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, FlagName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs the status report and returns a process exit code.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "MFILESEXPORTER_")
            .AddCommandLine(args)
            .Build();

        var options = config.GetSection(ExporterOptions.SectionName).Get<ExporterOptions>()
                   ?? new ExporterOptions();

        var connectionString = options.TrackingDatabase.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            System.Console.Error.WriteLine(
                "Cannot run --status: Exporter:TrackingDatabase:ConnectionString is not configured.");
            return 2;
        }

        return await RunAgainstAsync(connectionString, System.Console.Out, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Test-friendly entry point. Bypasses appsettings.json discovery and
    /// writes to the supplied <paramref name="output"/> so tests can assert
    /// on captured strings instead of process stdout.
    /// </summary>
    public static async Task<int> RunAgainstAsync(
        string connectionString,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            WriteHeader(output, "Status summary");
            await RunAndPrintAsync(output, conn, StatusSql, cancellationToken).ConfigureAwait(false);

            WriteHeader(output, "Outcomes");
            await RunAndPrintAsync(output, conn, OutcomesSql, cancellationToken).ConfigureAwait(false);

            WriteHeader(output, "Workers");
            await RunAndPrintAsync(output, conn, WorkersSql, cancellationToken).ConfigureAwait(false);

            WriteHeader(output, "Failures by category (top 10)");
            await RunAndPrintAsync(output, conn, FailuresSql, cancellationToken).ConfigureAwait(false);

            WriteHeader(output, "Checkpoint");
            await RunAndPrintAsync(output, conn, CheckpointSql, cancellationToken).ConfigureAwait(false);

            return 0;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"--status failed: {ex.Message}");
            return 1;
        }
    }

    // -----------------------------------------------------------------------
    // Rendering
    // -----------------------------------------------------------------------

    private static void WriteHeader(TextWriter output, string title)
    {
        output.WriteLine();
        var bar = new string('─', Math.Max(24, title.Length + 8));
        output.WriteLine(bar);
        output.WriteLine($" {title}");
        output.WriteLine(bar);
    }

    private static async Task RunAndPrintAsync(TextWriter output, SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var columns = new string[reader.FieldCount];
        var widths  = new int[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns[i] = reader.GetName(i);
            widths[i]  = columns[i].Length;
        }

        var rows = new List<string[]>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? "-" : reader.GetValue(i)?.ToString() ?? "-";
                if (value.Length > 60) value = string.Concat(value.AsSpan(0, 57), "…");
                row[i]    = value;
                widths[i] = Math.Max(widths[i], value.Length);
            }
            rows.Add(row);
        }

        if (rows.Count == 0)
        {
            output.WriteLine(" (no rows)");
            return;
        }

        // Header
        for (var i = 0; i < columns.Length; i++)
        {
            output.Write(columns[i].PadRight(widths[i] + 2));
        }
        output.WriteLine();
        for (var i = 0; i < columns.Length; i++)
        {
            output.Write(new string('-', widths[i]).PadRight(widths[i] + 2));
        }
        output.WriteLine();

        // Rows
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                output.Write(row[i].PadRight(widths[i] + 2));
            }
            output.WriteLine();
        }
    }

    // -----------------------------------------------------------------------
    // Inlined queries — matches deploy/reports/*.sql but distilled to the
    // columns worth showing in a fixed-width terminal.
    // -----------------------------------------------------------------------

    private const string TargetJobCte = @"
WITH TargetJob AS (
    SELECT TOP (1) ExportJobId
    FROM   dbo.ExportJobs
    WHERE  Status = N'Running'
    ORDER  BY StartedAtUtc DESC, ExportJobId DESC
)";

    private const string StatusSql = TargetJobCte + @"
SELECT
    s.JobName,
    s.PartitionKey                                           AS [Partition],
    s.Status,
    Expected      = ISNULL(s.TotalDocumentsExpected, 0),
    Processed     = ISNULL(s.TotalRecorded, 0),
    Remaining     = CASE WHEN s.TotalDocumentsExpected IS NULL THEN NULL
                         ELSE s.TotalDocumentsExpected - ISNULL(s.TotalRecorded, 0) END,
    Failed        = ISNULL(s.TotalFailed, 0),
    Skipped       = ISNULL(s.TotalSkipped, 0),
    [Docs/sec]    = CAST(ISNULL(s.DocumentsPerSecond, 0) AS DECIMAL(9,2)),
    [MiB/sec]     = CAST(ISNULL(s.MebibytesPerSecond, 0) AS DECIMAL(9,2)),
    PctComplete   = CASE WHEN s.TotalDocumentsExpected IS NULL OR s.TotalDocumentsExpected = 0 THEN NULL
                         ELSE CAST(100.0 * ISNULL(s.TotalRecorded,0) / s.TotalDocumentsExpected AS DECIMAL(5,2)) END,
    Workers       = ISNULL(s.ActiveWorkers,0),
    OpenErrors    = ISNULL(s.OpenErrors,0),
    Elapsed       = CONVERT(varchar(19), DATEADD(SECOND, s.ElapsedSeconds, 0), 108)
FROM   dbo.vw_JobSummary AS s
JOIN   TargetJob         AS t ON t.ExportJobId = s.ExportJobId;";

    private const string OutcomesSql = TargetJobCte + @",
Rollup AS (
    SELECT s.TotalRecorded, s.TotalSucceeded, s.TotalFailed, s.TotalSkipped
    FROM   dbo.vw_JobSummary AS s
    JOIN   TargetJob         AS t ON t.ExportJobId = s.ExportJobId
)
SELECT Outcome = N'Succeeded', Count = TotalSucceeded,
       PctOfTotal = CASE WHEN TotalRecorded=0 THEN NULL ELSE CAST(100.0*TotalSucceeded/TotalRecorded AS DECIMAL(5,2)) END
FROM   Rollup
UNION ALL SELECT N'Failed',   TotalFailed,
       CASE WHEN TotalRecorded=0 THEN NULL ELSE CAST(100.0*TotalFailed/TotalRecorded AS DECIMAL(5,2)) END
FROM   Rollup
UNION ALL SELECT N'Skipped',  TotalSkipped,
       CASE WHEN TotalRecorded=0 THEN NULL ELSE CAST(100.0*TotalSkipped/TotalRecorded AS DECIMAL(5,2)) END
FROM   Rollup;";

    private const string WorkersSql = TargetJobCte + @"
SELECT
    WorkerName        = h.WorkerName,
    MachineName       = h.MachineName,
    Status            = h.Status,
    Health            = h.HealthLabel,
    HeartbeatAgeSecs  = h.HeartbeatAgeSeconds,
    StartedAtUtc      = h.StartedAtUtc
FROM   dbo.vw_WorkerHealth AS h
JOIN   TargetJob           AS t ON t.ExportJobId = h.ExportJobId
ORDER  BY CASE h.HealthLabel
             WHEN N'Unhealthy' THEN 1
             WHEN N'Suspect'   THEN 2
             WHEN N'Unknown'   THEN 3
             WHEN N'Stopped'   THEN 4
             WHEN N'Healthy'   THEN 5
          END,
          h.WorkerName;";

    private const string FailuresSql = TargetJobCte + @"
SELECT TOP (10)
       ErrorCategory,
       ErrorSeverity,
       Count       = COUNT(*),
       LastSeenUtc = MAX(OccurredAtUtc),
       Sample      = LEFT(MAX(ErrorMessage), 60)
FROM   dbo.ExportErrors AS e
JOIN   TargetJob        AS t ON t.ExportJobId = e.ExportJobId
GROUP  BY ErrorCategory, ErrorSeverity
ORDER  BY COUNT(*) DESC;";

    private const string CheckpointSql = @"
SELECT PartitionKey,
       LastDocumentFilePartId,
       LastVersionPartId,
       DocumentsProcessedInPartition,
       CheckpointAtUtc,
       AgeSeconds
FROM   dbo.vw_CheckpointCurrent
ORDER  BY PartitionKey;";
}
