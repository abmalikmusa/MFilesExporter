/* =========================================================================
 * 07 · Active jobs
 *
 * Every job with Status = 'Running', across all partitions. Used when
 * multiple export runs share the same tracking database.
 * ========================================================================= */
SELECT
    ExportJobId,
    JobName,
    PartitionKey,
    Status,
    StartedAtUtc,
    ElapsedHours     = CAST(DATEDIFF(SECOND, StartedAtUtc, SYSUTCDATETIME()) / 3600.0 AS DECIMAL(9,2)),
    Expected         = ISNULL(TotalDocumentsExpected, 0),
    Processed        = ISNULL(TotalRecorded, 0),
    Failed           = ISNULL(TotalFailed, 0),
    ActiveWorkers    = ISNULL(ActiveWorkers, 0),
    OpenErrors       = ISNULL(OpenErrors, 0),
    DocsPerSec       = ISNULL(DocumentsPerSecond, 0)
FROM   dbo.vw_JobSummary
WHERE  Status = N'Running'
ORDER  BY StartedAtUtc DESC;
