/* =========================================================================
 * 02 · Outcomes breakdown
 *
 * Terminal outcomes for the target job, with each row's share of the
 * total processed count. Used for a quick sanity check ("failure rate?").
 * ========================================================================= */
DECLARE @JobId BIGINT = NULL;

WITH TargetJob AS
(
    SELECT TOP (1) ExportJobId
    FROM   dbo.ExportJobs
    WHERE  (@JobId IS NULL AND Status = N'Running')
        OR (ExportJobId = @JobId)
    ORDER  BY StartedAtUtc DESC, ExportJobId DESC
),
Rollup AS
(
    SELECT s.ExportJobId,
           s.TotalRecorded,
           s.TotalSucceeded,
           s.TotalFailed,
           s.TotalSkipped
    FROM   dbo.vw_JobSummary AS s
    JOIN   TargetJob         AS t ON t.ExportJobId = s.ExportJobId
)
SELECT Outcome     = N'Succeeded',
       Count       = TotalSucceeded,
       PctOfTotal  = CASE WHEN TotalRecorded = 0 THEN NULL
                          ELSE CAST(100.0 * TotalSucceeded / TotalRecorded AS DECIMAL(5,2)) END
FROM   Rollup
UNION ALL
SELECT N'Failed',    TotalFailed,
       CASE WHEN TotalRecorded = 0 THEN NULL
            ELSE CAST(100.0 * TotalFailed / TotalRecorded AS DECIMAL(5,2)) END
FROM   Rollup
UNION ALL
SELECT N'Skipped',   TotalSkipped,
       CASE WHEN TotalRecorded = 0 THEN NULL
            ELSE CAST(100.0 * TotalSkipped / TotalRecorded AS DECIMAL(5,2)) END
FROM   Rollup;
