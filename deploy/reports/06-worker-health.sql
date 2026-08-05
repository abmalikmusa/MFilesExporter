/* =========================================================================
 * 06 · Worker health
 *
 * One row per worker with heartbeat freshness and the derived health
 * label (Healthy · Suspect · Unhealthy · Stopped · Unknown).
 *
 * Suspect = heartbeat older than 120 s (configurable at the view level).
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
    JobName            = h.JobName,
    WorkerName         = h.WorkerName,
    MachineName        = h.MachineName,
    Partition          = h.AssignedPartition,
    Concurrency        = h.Concurrency,
    Status             = h.Status,
    HealthLabel        = h.HealthLabel,
    StartedAtUtc       = h.StartedAtUtc,
    LastHeartbeatUtc   = h.LastHeartbeatUtc,
    HeartbeatAgeSecs   = h.HeartbeatAgeSeconds,
    StoppedAtUtc       = h.StoppedAtUtc
FROM   dbo.vw_WorkerHealth AS h
JOIN   TargetJob           AS t ON t.ExportJobId = h.ExportJobId
ORDER  BY
    CASE h.HealthLabel
        WHEN N'Unhealthy' THEN 1
        WHEN N'Suspect'   THEN 2
        WHEN N'Unknown'   THEN 3
        WHEN N'Stopped'   THEN 4
        WHEN N'Healthy'   THEN 5
    END,
    h.WorkerName;
