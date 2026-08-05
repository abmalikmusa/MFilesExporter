/* =========================================================================
 * 04 · Throughput per hour
 *
 * Docs/sec and MiB/sec bucketed by hour, sourced from the periodic
 * ExportProgress snapshots. Useful for spotting sudden slowdowns.
 * ========================================================================= */
DECLARE @JobId BIGINT = NULL;

WITH TargetJob AS
(
    SELECT TOP (1) ExportJobId
    FROM   dbo.ExportJobs
    WHERE  (@JobId IS NULL AND Status IN (N'Running', N'Completed'))
        OR (ExportJobId = @JobId)
    ORDER  BY StartedAtUtc DESC, ExportJobId DESC
)
SELECT
    HourStartUtc   = th.HourStartUtc,
    DocsProcessed  = th.DocsProcessed,
    BytesWritten   = th.BytesWritten,
    AvgDocsPerSec  = CAST(th.DocsProcessed / 3600.0 AS DECIMAL(10,2)),
    AvgMiBPerSec   = CAST(th.BytesWritten / 3600.0 / 1048576.0 AS DECIMAL(10,3))
FROM   dbo.vw_ThroughputHourly AS th
JOIN   TargetJob               AS t ON t.ExportJobId = th.ExportJobId
ORDER  BY th.HourStartUtc DESC;
