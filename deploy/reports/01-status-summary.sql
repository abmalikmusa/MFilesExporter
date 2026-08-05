/* =========================================================================
 * 01 · Status summary
 *
 * One row per matching job. Answers "what's processed, what's outstanding,
 * are workers alive, are there open errors?" in a single glance.
 *
 * Parameters
 *   @JobId — leave NULL to pick the most recent Running job (or the
 *            most recent job of any status if nothing is currently
 *            running).
 * ========================================================================= */
DECLARE @JobId BIGINT = NULL;

WITH TargetJob AS
(
    SELECT TOP (1) ExportJobId
    FROM   dbo.ExportJobs
    WHERE  (@JobId IS NULL AND Status = N'Running')
        OR (ExportJobId = @JobId)
    ORDER  BY StartedAtUtc DESC, ExportJobId DESC
)
SELECT
    s.JobName,
    s.PartitionKey,
    s.Status,
    s.StartedAtUtc,
    s.CompletedAtUtc,
    Elapsed         = CONVERT(varchar(19), DATEADD(SECOND, s.ElapsedSeconds, 0), 108),
    Expected        = ISNULL(s.TotalDocumentsExpected, 0),
    Processed       = ISNULL(s.TotalRecorded, 0),
    Remaining       = CASE WHEN s.TotalDocumentsExpected IS NULL THEN NULL
                           ELSE s.TotalDocumentsExpected - ISNULL(s.TotalRecorded, 0) END,
    Succeeded       = ISNULL(s.TotalSucceeded, 0),
    Failed          = ISNULL(s.TotalFailed, 0),
    Skipped         = ISNULL(s.TotalSkipped, 0),
    BytesWritten    = ISNULL(s.TotalBytesWritten, 0),
    DocsPerSec      = ISNULL(s.DocumentsPerSecond, 0),
    MiBPerSec       = ISNULL(s.MebibytesPerSecond, 0),
    PctComplete     = CASE
                        WHEN s.TotalDocumentsExpected IS NULL OR s.TotalDocumentsExpected = 0 THEN NULL
                        ELSE CAST(100.0 * ISNULL(s.TotalRecorded, 0) / s.TotalDocumentsExpected AS DECIMAL(5,2))
                      END,
    ETASeconds      = CASE
                        WHEN s.DocumentsPerSecond IS NULL OR s.DocumentsPerSecond <= 0 THEN NULL
                        WHEN s.TotalDocumentsExpected IS NULL THEN NULL
                        ELSE CAST((s.TotalDocumentsExpected - ISNULL(s.TotalRecorded, 0)) / s.DocumentsPerSecond AS BIGINT)
                      END,
    ActiveWorkers   = ISNULL(s.ActiveWorkers, 0),
    TotalWorkers    = ISNULL(s.TotalWorkers, 0),
    OpenErrors      = ISNULL(s.OpenErrors, 0),
    CriticalErrors  = ISNULL(s.CriticalErrors, 0)
FROM   dbo.vw_JobSummary AS s
JOIN   TargetJob         AS t ON t.ExportJobId = s.ExportJobId;
