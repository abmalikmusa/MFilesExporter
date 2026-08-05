/****************************************************************************
 * File:        40-views.sql
 * Purpose:     Read-only, dashboard-friendly views. Named vw_* by convention.
 *              These are the recommended surface for BI tools; direct table
 *              access from BI is discouraged so schema evolution stays safe.
 ****************************************************************************/

USE [MFilesExportTracking];
GO

SET NOCOUNT ON;
GO

/* =========================================================================
 * vw_ActiveJobs — currently running jobs with their most recent snapshot.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.vw_ActiveJobs', N'V') IS NOT NULL DROP VIEW dbo.vw_ActiveJobs;
GO
CREATE VIEW dbo.vw_ActiveJobs
AS
    SELECT
        j.ExportJobId,
        j.JobName,
        j.PartitionKey,
        j.SourceServer,
        j.SourceDatabase,
        j.StartedAtUtc,
        j.TotalDocumentsExpected,
        j.Status,
        latest.SnapshotAtUtc               AS LastSnapshotAtUtc,
        latest.TotalRecorded,
        latest.TotalSucceeded,
        latest.TotalFailed,
        latest.TotalSkipped,
        latest.TotalBytesWritten,
        latest.DocumentsPerSecond,
        latest.MebibytesPerSecond,
        latest.LastDocumentFilePartId,
        latest.LastVersionPartId,
        DATEDIFF(SECOND, j.StartedAtUtc, SYSUTCDATETIME()) AS ElapsedSeconds
    FROM dbo.ExportJobs AS j
    OUTER APPLY
    (
        SELECT TOP (1) p.*
        FROM dbo.ExportProgress AS p
        WHERE p.ExportJobId = j.ExportJobId
        ORDER BY p.SnapshotAtUtc DESC, p.ExportProgressId DESC
    ) AS latest
    WHERE j.Status IN (N'Running', N'Paused');
GO

/* =========================================================================
 * vw_JobSummary — every job with rollups from progress + errors + workers.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.vw_JobSummary', N'V') IS NOT NULL DROP VIEW dbo.vw_JobSummary;
GO
CREATE VIEW dbo.vw_JobSummary
AS
    SELECT
        j.ExportJobId,
        j.JobName,
        j.PartitionKey,
        j.Status,
        j.StartedAtUtc,
        j.CompletedAtUtc,
        DATEDIFF(SECOND, j.StartedAtUtc, ISNULL(j.CompletedAtUtc, SYSUTCDATETIME())) AS ElapsedSeconds,
        j.TotalDocumentsExpected,
        latest.TotalRecorded,
        latest.TotalSucceeded,
        latest.TotalFailed,
        latest.TotalSkipped,
        latest.TotalBytesWritten,
        latest.DocumentsPerSecond,
        latest.MebibytesPerSecond,
        w.ActiveWorkers,
        w.TotalWorkers,
        e.OpenErrors,
        e.CriticalErrors
    FROM dbo.ExportJobs AS j
    OUTER APPLY
    (
        SELECT TOP (1) p.TotalRecorded, p.TotalSucceeded, p.TotalFailed,
                       p.TotalSkipped, p.TotalBytesWritten,
                       p.DocumentsPerSecond, p.MebibytesPerSecond
        FROM dbo.ExportProgress AS p
        WHERE p.ExportJobId = j.ExportJobId
        ORDER BY p.SnapshotAtUtc DESC, p.ExportProgressId DESC
    ) AS latest
    OUTER APPLY
    (
        SELECT
            SUM(CASE WHEN Status IN (N'Active', N'Idle') THEN 1 ELSE 0 END) AS ActiveWorkers,
            COUNT(*)                                                        AS TotalWorkers
        FROM dbo.ExportWorkers WHERE ExportJobId = j.ExportJobId
    ) AS w
    OUTER APPLY
    (
        SELECT
            SUM(CASE WHEN Status IN (N'New', N'Acknowledged', N'Investigating') THEN 1 ELSE 0 END) AS OpenErrors,
            SUM(CASE WHEN ErrorSeverity = N'Critical' THEN 1 ELSE 0 END)                          AS CriticalErrors
        FROM dbo.ExportErrors WHERE ExportJobId = j.ExportJobId
    ) AS e;
GO

/* =========================================================================
 * vw_WorkerHealth — one row per worker with heartbeat freshness.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.vw_WorkerHealth', N'V') IS NOT NULL DROP VIEW dbo.vw_WorkerHealth;
GO
CREATE VIEW dbo.vw_WorkerHealth
AS
    SELECT
        w.ExportWorkerId,
        w.ExportJobId,
        j.JobName,
        w.WorkerName,
        w.MachineName,
        w.AssignedPartition,
        w.Concurrency,
        w.Status,
        w.StartedAtUtc,
        w.LastHeartbeatUtc,
        w.StoppedAtUtc,
        DATEDIFF(SECOND, w.LastHeartbeatUtc, SYSUTCDATETIME()) AS HeartbeatAgeSeconds,
        CASE
            WHEN w.Status = N'Stopped' THEN N'Stopped'
            WHEN w.Status IN (N'Failed', N'Stalled') THEN N'Unhealthy'
            WHEN w.LastHeartbeatUtc IS NULL THEN N'Unknown'
            WHEN DATEDIFF(SECOND, w.LastHeartbeatUtc, SYSUTCDATETIME()) > 120 THEN N'Suspect'
            ELSE N'Healthy'
        END AS HealthLabel
    FROM dbo.ExportWorkers AS w
    INNER JOIN dbo.ExportJobs AS j ON j.ExportJobId = w.ExportJobId;
GO

/* =========================================================================
 * vw_ErrorSummary — errors grouped by category/severity for a dashboard.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.vw_ErrorSummary', N'V') IS NOT NULL DROP VIEW dbo.vw_ErrorSummary;
GO
CREATE VIEW dbo.vw_ErrorSummary
AS
    SELECT
        ExportJobId,
        ErrorCategory,
        ErrorSeverity,
        Status,
        COUNT(*) AS ErrorCount,
        MIN(OccurredAtUtc) AS FirstOccurredAtUtc,
        MAX(OccurredAtUtc) AS LastOccurredAtUtc
    FROM dbo.ExportErrors
    GROUP BY ExportJobId, ErrorCategory, ErrorSeverity, Status;
GO

/* =========================================================================
 * vw_ThroughputHourly — hourly rollup of throughput per job.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.vw_ThroughputHourly', N'V') IS NOT NULL DROP VIEW dbo.vw_ThroughputHourly;
GO
CREATE VIEW dbo.vw_ThroughputHourly
AS
    SELECT
        ExportJobId,
        DATEADD(HOUR, DATEDIFF(HOUR, 0, SnapshotAtUtc), 0) AS BucketStartUtc,
        AVG(DocumentsPerSecond) AS AvgDocsPerSec,
        AVG(MebibytesPerSecond) AS AvgMibPerSec,
        MAX(TotalRecorded)      AS EndOfHourRecorded,
        MAX(TotalSucceeded)     AS EndOfHourSucceeded,
        MAX(TotalBytesWritten)  AS EndOfHourBytesWritten
    FROM dbo.ExportProgress
    WHERE SnapshotAtUtc IS NOT NULL
    GROUP BY ExportJobId, DATEADD(HOUR, DATEDIFF(HOUR, 0, SnapshotAtUtc), 0);
GO

/* =========================================================================
 * vw_CheckpointCurrent — one row per (job, partition) with Active cursor.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.vw_CheckpointCurrent', N'V') IS NOT NULL DROP VIEW dbo.vw_CheckpointCurrent;
GO
CREATE VIEW dbo.vw_CheckpointCurrent
AS
    SELECT
        c.ExportCheckpointId,
        c.ExportJobId,
        j.JobName,
        c.PartitionKey,
        c.LastDocumentFilePartId,
        c.LastVersionPartId,
        c.DocumentsProcessedInPartition,
        c.CheckpointAtUtc,
        DATEDIFF(SECOND, c.CheckpointAtUtc, SYSUTCDATETIME()) AS AgeSeconds
    FROM dbo.ExportCheckpoints AS c
    INNER JOIN dbo.ExportJobs AS j ON j.ExportJobId = c.ExportJobId
    WHERE c.Status = N'Active';
GO

/* =========================================================================
 * vw_AuditRecent — last 1 000 audit events, most recent first.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.vw_AuditRecent', N'V') IS NOT NULL DROP VIEW dbo.vw_AuditRecent;
GO
CREATE VIEW dbo.vw_AuditRecent
AS
    SELECT TOP (1000)
        ExportAuditId,
        ExportJobId,
        EntityType,
        EntityId,
        AuditAction,
        PreviousStatus,
        NewStatus,
        ActionDetails,
        ActorName,
        ActorType,
        OccurredAtUtc
    FROM dbo.ExportAudit
    ORDER BY OccurredAtUtc DESC, ExportAuditId DESC;
GO
