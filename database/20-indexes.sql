/****************************************************************************
 * File:        20-indexes.sql
 * Purpose:     Non-clustered indexes optimised for the actual query patterns
 *              the exporter and operator dashboards issue. Every index has a
 *              named comment describing the query it supports.
 *
 * Naming:      IX_<Table>_<ColumnsSeparatedByUnderscore>[_INCL_<incl>]
 *              UX_<Table>_...      for unique indexes
 ****************************************************************************/

USE [MFilesExportTracking];
GO

SET NOCOUNT ON;
GO

/* -------------------------------------------------------------------------
 * ExportJobs
 * ------------------------------------------------------------------------- */
-- "list running jobs" (dashboard)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportJobs_Status_StartedAtUtc' AND object_id = OBJECT_ID(N'dbo.ExportJobs'))
    CREATE INDEX IX_ExportJobs_Status_StartedAtUtc
        ON dbo.ExportJobs (Status, StartedAtUtc DESC)
        INCLUDE (JobName, PartitionKey, CompletedAtUtc);

-- "find a job by partition"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportJobs_PartitionKey_Status' AND object_id = OBJECT_ID(N'dbo.ExportJobs'))
    CREATE INDEX IX_ExportJobs_PartitionKey_Status
        ON dbo.ExportJobs (PartitionKey, Status);

/* -------------------------------------------------------------------------
 * ExportWorkers
 * ------------------------------------------------------------------------- */
-- "workers under this job"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportWorkers_JobId_Status' AND object_id = OBJECT_ID(N'dbo.ExportWorkers'))
    CREATE INDEX IX_ExportWorkers_JobId_Status
        ON dbo.ExportWorkers (ExportJobId, Status)
        INCLUDE (WorkerName, MachineName, LastHeartbeatUtc);

-- "which workers have gone stale?" (heartbeat monitor)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportWorkers_Status_LastHeartbeatUtc' AND object_id = OBJECT_ID(N'dbo.ExportWorkers'))
    CREATE INDEX IX_ExportWorkers_Status_LastHeartbeatUtc
        ON dbo.ExportWorkers (Status, LastHeartbeatUtc)
        WHERE Status IN (N'Registered', N'Active', N'Idle');

/* -------------------------------------------------------------------------
 * ExportProgress
 * ------------------------------------------------------------------------- */
-- "most recent snapshot for a job" (dashboard live tile)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportProgress_JobId_SnapshotAtUtc' AND object_id = OBJECT_ID(N'dbo.ExportProgress'))
    CREATE INDEX IX_ExportProgress_JobId_SnapshotAtUtc
        ON dbo.ExportProgress (ExportJobId, SnapshotAtUtc DESC)
        INCLUDE (TotalRecorded, TotalSucceeded, TotalFailed, TotalSkipped, TotalBytesWritten);

-- "throughput per worker over time"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportProgress_WorkerId_SnapshotAtUtc' AND object_id = OBJECT_ID(N'dbo.ExportProgress'))
    CREATE INDEX IX_ExportProgress_WorkerId_SnapshotAtUtc
        ON dbo.ExportProgress (ExportWorkerId, SnapshotAtUtc DESC)
        WHERE ExportWorkerId IS NOT NULL;

/* -------------------------------------------------------------------------
 * ExportMetrics
 * ------------------------------------------------------------------------- */
-- "time-series for a metric" (chart panel)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportMetrics_Name_CapturedAtUtc' AND object_id = OBJECT_ID(N'dbo.ExportMetrics'))
    CREATE INDEX IX_ExportMetrics_Name_CapturedAtUtc
        ON dbo.ExportMetrics (MetricName, CapturedAtUtc)
        INCLUDE (ExportJobId, MetricValue, MetricUnit);

-- "all metrics for a job"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportMetrics_JobId_CapturedAtUtc' AND object_id = OBJECT_ID(N'dbo.ExportMetrics'))
    CREATE INDEX IX_ExportMetrics_JobId_CapturedAtUtc
        ON dbo.ExportMetrics (ExportJobId, CapturedAtUtc)
        INCLUDE (MetricName, MetricValue);

-- retention scan: "metrics older than N days"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportMetrics_CapturedAtUtc_Status' AND object_id = OBJECT_ID(N'dbo.ExportMetrics'))
    CREATE INDEX IX_ExportMetrics_CapturedAtUtc_Status
        ON dbo.ExportMetrics (CapturedAtUtc)
        INCLUDE (Status);

/* -------------------------------------------------------------------------
 * ExportErrors
 * ------------------------------------------------------------------------- */
-- "open errors for a job by severity"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportErrors_JobId_Status_Severity' AND object_id = OBJECT_ID(N'dbo.ExportErrors'))
    CREATE INDEX IX_ExportErrors_JobId_Status_Severity
        ON dbo.ExportErrors (ExportJobId, Status, ErrorSeverity, OccurredAtUtc DESC)
        INCLUDE (ExceptionType, ErrorMessage);

-- "errors for a specific document" (rerun helper)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportErrors_DocumentFilePart_VersionPart' AND object_id = OBJECT_ID(N'dbo.ExportErrors'))
    CREATE INDEX IX_ExportErrors_DocumentFilePart_VersionPart
        ON dbo.ExportErrors (DocumentFilePartId, VersionPartId)
        WHERE DocumentFilePartId IS NOT NULL;

-- "errors for a specific idempotency key"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportErrors_IdempotencyKey' AND object_id = OBJECT_ID(N'dbo.ExportErrors'))
    CREATE INDEX IX_ExportErrors_IdempotencyKey
        ON dbo.ExportErrors (IdempotencyKey)
        WHERE IdempotencyKey IS NOT NULL;

/* -------------------------------------------------------------------------
 * ExportCheckpoints
 *   The filtered unique index UX_ExportCheckpoints_Active_JobPartition is
 *   defined in 10-tables.sql. Add history-lookup index here.
 * ------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportCheckpoints_JobId_CheckpointAtUtc' AND object_id = OBJECT_ID(N'dbo.ExportCheckpoints'))
    CREATE INDEX IX_ExportCheckpoints_JobId_CheckpointAtUtc
        ON dbo.ExportCheckpoints (ExportJobId, CheckpointAtUtc DESC)
        INCLUDE (PartitionKey, LastDocumentFilePartId, LastVersionPartId, Status);

/* -------------------------------------------------------------------------
 * ExportAudit
 * ------------------------------------------------------------------------- */
-- "audit trail for an entity" (drill-down)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportAudit_EntityType_EntityId_OccurredAtUtc' AND object_id = OBJECT_ID(N'dbo.ExportAudit'))
    CREATE INDEX IX_ExportAudit_EntityType_EntityId_OccurredAtUtc
        ON dbo.ExportAudit (EntityType, EntityId, OccurredAtUtc DESC)
        INCLUDE (AuditAction, PreviousStatus, NewStatus, ActorName);

-- "recent audit events for a job"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExportAudit_JobId_OccurredAtUtc' AND object_id = OBJECT_ID(N'dbo.ExportAudit'))
    CREATE INDEX IX_ExportAudit_JobId_OccurredAtUtc
        ON dbo.ExportAudit (ExportJobId, OccurredAtUtc DESC)
        WHERE ExportJobId IS NOT NULL;
GO
