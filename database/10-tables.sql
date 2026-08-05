/****************************************************************************
 * File:        10-tables.sql
 * Purpose:     Creates all seven core tracking tables plus their archive
 *              shadows on the ArchiveFG filegroup.
 *
 * Conventions:
 *   * Every table has: PK, Status (CHECK-constrained), CreatedDate,
 *     ModifiedDate, CreatedBy, ModifiedBy, RowVersion (concurrency token).
 *   * Every FK is declared with ON DELETE NO ACTION to preserve audit trail.
 *   * DATETIME2(3) is used everywhere — millisecond precision, 8 bytes.
 *   * SYSUTCDATETIME() defaults ensure UTC-only timestamps.
 *   * CHAR(64) is used for hex-encoded SHA-256 idempotency keys.
 ****************************************************************************/

USE [MFilesExportTracking];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* =========================================================================
 * 1. dbo.ExportJobs — parent entity: one row per export run.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.ExportJobs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExportJobs
    (
        ExportJobId              BIGINT           IDENTITY(1,1) NOT NULL,
        JobName                  NVARCHAR(200)    NOT NULL,
        SourceServer             NVARCHAR(256)    NOT NULL,
        SourceDatabase           NVARCHAR(256)    NOT NULL,
        PartitionKey             NVARCHAR(100)    NOT NULL
                                                  CONSTRAINT DF_ExportJobs_PartitionKey DEFAULT (N'default'),
        TotalDocumentsExpected   BIGINT           NULL,
        StartedAtUtc             DATETIME2(3)     NULL,
        CompletedAtUtc           DATETIME2(3)     NULL,
        CancellationReason       NVARCHAR(2000)   NULL,
        Status                   NVARCHAR(32)     NOT NULL
                                                  CONSTRAINT DF_ExportJobs_Status DEFAULT (N'Pending'),
        CreatedDate              DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportJobs_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy                NVARCHAR(128)    NOT NULL
                                                  CONSTRAINT DF_ExportJobs_CreatedBy DEFAULT (SUSER_SNAME()),
        ModifiedDate             DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportJobs_ModifiedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedBy               NVARCHAR(128)    NOT NULL
                                                  CONSTRAINT DF_ExportJobs_ModifiedBy DEFAULT (SUSER_SNAME()),
        RowVersion               ROWVERSION       NOT NULL,

        CONSTRAINT PK_ExportJobs
            PRIMARY KEY CLUSTERED (ExportJobId),
        CONSTRAINT UQ_ExportJobs_Name_Partition
            UNIQUE (JobName, PartitionKey),
        CONSTRAINT CK_ExportJobs_Status
            CHECK (Status IN (N'Pending', N'Running', N'Paused', N'Completed', N'Failed', N'Cancelled', N'Archived')),
        CONSTRAINT CK_ExportJobs_CompletedAfterStart
            CHECK (CompletedAtUtc IS NULL OR StartedAtUtc IS NULL OR CompletedAtUtc >= StartedAtUtc)
    );
END
GO

/* =========================================================================
 * 2. dbo.ExportWorkers — worker instances registered under a job.
 *    A job may have many workers (horizontal scaling by partition).
 * ========================================================================= */
IF OBJECT_ID(N'dbo.ExportWorkers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExportWorkers
    (
        ExportWorkerId           BIGINT           IDENTITY(1,1) NOT NULL,
        ExportJobId              BIGINT           NOT NULL,
        WorkerName               NVARCHAR(200)    NOT NULL,
        MachineName              NVARCHAR(200)    NOT NULL,
        ProcessId                INT              NULL,
        AssignedPartition        NVARCHAR(100)    NOT NULL,
        Concurrency              INT              NOT NULL
                                                  CONSTRAINT DF_ExportWorkers_Concurrency DEFAULT (1),
        StartedAtUtc             DATETIME2(3)     NULL,
        LastHeartbeatUtc         DATETIME2(3)     NULL,
        StoppedAtUtc             DATETIME2(3)     NULL,
        Status                   NVARCHAR(32)     NOT NULL
                                                  CONSTRAINT DF_ExportWorkers_Status DEFAULT (N'Registered'),
        CreatedDate              DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportWorkers_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy                NVARCHAR(128)    NOT NULL
                                                  CONSTRAINT DF_ExportWorkers_CreatedBy DEFAULT (SUSER_SNAME()),
        ModifiedDate             DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportWorkers_ModifiedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedBy               NVARCHAR(128)    NOT NULL
                                                  CONSTRAINT DF_ExportWorkers_ModifiedBy DEFAULT (SUSER_SNAME()),
        RowVersion               ROWVERSION       NOT NULL,

        CONSTRAINT PK_ExportWorkers
            PRIMARY KEY CLUSTERED (ExportWorkerId),
        CONSTRAINT FK_ExportWorkers_Job
            FOREIGN KEY (ExportJobId) REFERENCES dbo.ExportJobs (ExportJobId)
            ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT UQ_ExportWorkers_Job_Partition_Worker
            UNIQUE (ExportJobId, AssignedPartition, WorkerName, MachineName),
        CONSTRAINT CK_ExportWorkers_Status
            CHECK (Status IN (N'Registered', N'Active', N'Idle', N'Stalled', N'Stopped', N'Failed', N'Archived')),
        CONSTRAINT CK_ExportWorkers_Concurrency
            CHECK (Concurrency BETWEEN 1 AND 256)
    );
END
GO

/* =========================================================================
 * 3. dbo.ExportProgress — append-only aggregate progress snapshots.
 *    Each row is a point-in-time snapshot of counters for a job. Kept
 *    append-only so we retain the throughput history for capacity planning.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.ExportProgress', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExportProgress
    (
        ExportProgressId         BIGINT           IDENTITY(1,1) NOT NULL,
        ExportJobId              BIGINT           NOT NULL,
        ExportWorkerId           BIGINT           NULL,
        SnapshotAtUtc            DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportProgress_SnapshotAtUtc DEFAULT (SYSUTCDATETIME()),
        TotalRecorded            BIGINT           NOT NULL CONSTRAINT DF_ExportProgress_TotalRecorded  DEFAULT (0),
        TotalSucceeded           BIGINT           NOT NULL CONSTRAINT DF_ExportProgress_TotalSucceeded DEFAULT (0),
        TotalFailed              BIGINT           NOT NULL CONSTRAINT DF_ExportProgress_TotalFailed    DEFAULT (0),
        TotalSkipped             BIGINT           NOT NULL CONSTRAINT DF_ExportProgress_TotalSkipped   DEFAULT (0),
        TotalBytesWritten        BIGINT           NOT NULL CONSTRAINT DF_ExportProgress_TotalBytes     DEFAULT (0),
        DocumentsPerSecond       DECIMAL(18,4)    NULL,
        MebibytesPerSecond       DECIMAL(18,4)    NULL,
        LastDocumentFilePartId   BIGINT           NULL,
        LastVersionPartId        BIGINT           NULL,
        Status                   NVARCHAR(32)     NOT NULL
                                                  CONSTRAINT DF_ExportProgress_Status DEFAULT (N'Snapshot'),
        CreatedDate              DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportProgress_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy                NVARCHAR(128)    NOT NULL
                                                  CONSTRAINT DF_ExportProgress_CreatedBy DEFAULT (SUSER_SNAME()),
        ModifiedDate             DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportProgress_ModifiedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedBy               NVARCHAR(128)    NOT NULL
                                                  CONSTRAINT DF_ExportProgress_ModifiedBy DEFAULT (SUSER_SNAME()),
        RowVersion               ROWVERSION       NOT NULL,

        CONSTRAINT PK_ExportProgress
            PRIMARY KEY CLUSTERED (ExportProgressId),
        CONSTRAINT FK_ExportProgress_Job
            FOREIGN KEY (ExportJobId) REFERENCES dbo.ExportJobs (ExportJobId)
            ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ExportProgress_Worker
            FOREIGN KEY (ExportWorkerId) REFERENCES dbo.ExportWorkers (ExportWorkerId)
            ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT CK_ExportProgress_Status
            CHECK (Status IN (N'Snapshot', N'Historical', N'Archived')),
        CONSTRAINT CK_ExportProgress_Counters_NonNegative
            CHECK (TotalRecorded >= 0
               AND TotalSucceeded >= 0
               AND TotalFailed >= 0
               AND TotalSkipped >= 0
               AND TotalBytesWritten >= 0),
        CONSTRAINT CK_ExportProgress_Consistency
            CHECK (TotalRecorded >= TotalSucceeded + TotalFailed + TotalSkipped
                OR TotalRecorded = 0)
    );
END
GO

/* =========================================================================
 * 4. dbo.ExportMetrics — time-series metric samples (throughput histograms,
 *    latency samples, gauge readings) captured by workers.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.ExportMetrics', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExportMetrics
    (
        ExportMetricId           BIGINT           IDENTITY(1,1) NOT NULL,
        ExportJobId              BIGINT           NOT NULL,
        ExportWorkerId           BIGINT           NULL,
        MetricName               NVARCHAR(200)    NOT NULL,
        MetricValue              FLOAT            NOT NULL,
        MetricUnit               NVARCHAR(50)     NOT NULL
                                                  CONSTRAINT DF_ExportMetrics_MetricUnit DEFAULT (N''),
        Tags                     NVARCHAR(2000)   NULL,   -- JSON key/value pairs
        CapturedAtUtc            DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportMetrics_CapturedAtUtc DEFAULT (SYSUTCDATETIME()),
        Status                   NVARCHAR(32)     NOT NULL
                                                  CONSTRAINT DF_ExportMetrics_Status DEFAULT (N'Live'),
        CreatedDate              DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportMetrics_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy                NVARCHAR(128)    NOT NULL
                                                  CONSTRAINT DF_ExportMetrics_CreatedBy DEFAULT (SUSER_SNAME()),
        ModifiedDate             DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportMetrics_ModifiedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedBy               NVARCHAR(128)    NOT NULL
                                                  CONSTRAINT DF_ExportMetrics_ModifiedBy DEFAULT (SUSER_SNAME()),
        RowVersion               ROWVERSION       NOT NULL,

        CONSTRAINT PK_ExportMetrics
            PRIMARY KEY CLUSTERED (ExportMetricId),
        CONSTRAINT FK_ExportMetrics_Job
            FOREIGN KEY (ExportJobId) REFERENCES dbo.ExportJobs (ExportJobId)
            ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ExportMetrics_Worker
            FOREIGN KEY (ExportWorkerId) REFERENCES dbo.ExportWorkers (ExportWorkerId)
            ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT CK_ExportMetrics_Status
            CHECK (Status IN (N'Live', N'RolledUp', N'Archived')),
        CONSTRAINT CK_ExportMetrics_Tags_IsJson
            CHECK (Tags IS NULL OR ISJSON(Tags) = 1)
    );
END
GO

/* =========================================================================
 * 5. dbo.ExportErrors — per-error log; one row per observed failure.
 *    Ties back to the affected document (part/version/data-file-version and
 *    the SHA-256 idempotency key) so operators can retry precisely.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.ExportErrors', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExportErrors
    (
        ExportErrorId            BIGINT           IDENTITY(1,1) NOT NULL,
        ExportJobId              BIGINT           NOT NULL,
        ExportWorkerId           BIGINT           NULL,
        DocumentFilePartId       BIGINT           NULL,
        VersionPartId            BIGINT           NULL,
        DataFileVersionId        BIGINT           NULL,
        IdempotencyKey           CHAR(64)         NULL,     -- lowercase hex SHA-256
        ErrorSeverity            NVARCHAR(16)     NOT NULL
                                                  CONSTRAINT DF_ExportErrors_ErrorSeverity DEFAULT (N'Error'),
        ErrorCategory            NVARCHAR(32)     NOT NULL
                                                  CONSTRAINT DF_ExportErrors_ErrorCategory DEFAULT (N'Unknown'),
        ErrorSource              NVARCHAR(200)    NOT NULL,   -- e.g. "ContentReaderStage"
        ExceptionType            NVARCHAR(400)    NULL,
        ErrorMessage             NVARCHAR(4000)   NOT NULL,
        StackTrace               NVARCHAR(MAX)    NULL,
        AttemptNumber            INT              NOT NULL
                                                  CONSTRAINT DF_ExportErrors_AttemptNumber DEFAULT (1),
        OccurredAtUtc            DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportErrors_OccurredAtUtc DEFAULT (SYSUTCDATETIME()),
        ResolvedAtUtc            DATETIME2(3)     NULL,
        ResolvedBy               NVARCHAR(128)    NULL,
        ResolutionNotes          NVARCHAR(2000)   NULL,
        Status                   NVARCHAR(32)     NOT NULL
                                                  CONSTRAINT DF_ExportErrors_Status DEFAULT (N'New'),
        CreatedDate              DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportErrors_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy                NVARCHAR(128)    NOT NULL
                                                  CONSTRAINT DF_ExportErrors_CreatedBy DEFAULT (SUSER_SNAME()),
        ModifiedDate             DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportErrors_ModifiedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedBy               NVARCHAR(128)    NOT NULL
                                                  CONSTRAINT DF_ExportErrors_ModifiedBy DEFAULT (SUSER_SNAME()),
        RowVersion               ROWVERSION       NOT NULL,

        CONSTRAINT PK_ExportErrors
            PRIMARY KEY CLUSTERED (ExportErrorId),
        CONSTRAINT FK_ExportErrors_Job
            FOREIGN KEY (ExportJobId) REFERENCES dbo.ExportJobs (ExportJobId)
            ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ExportErrors_Worker
            FOREIGN KEY (ExportWorkerId) REFERENCES dbo.ExportWorkers (ExportWorkerId)
            ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT CK_ExportErrors_Severity
            CHECK (ErrorSeverity IN (N'Warning', N'Error', N'Critical')),
        CONSTRAINT CK_ExportErrors_Category
            CHECK (ErrorCategory IN (N'Transient', N'Deterministic', N'Configuration', N'Security', N'Storage', N'Unknown')),
        CONSTRAINT CK_ExportErrors_Status
            CHECK (Status IN (N'New', N'Acknowledged', N'Investigating', N'Resolved', N'Ignored', N'Archived')),
        CONSTRAINT CK_ExportErrors_Attempt
            CHECK (AttemptNumber >= 1),
        CONSTRAINT CK_ExportErrors_ResolvedShape
            CHECK (
                (Status IN (N'Resolved', N'Ignored') AND ResolvedAtUtc IS NOT NULL AND ResolvedBy IS NOT NULL)
             OR (Status NOT IN (N'Resolved', N'Ignored') AND ResolvedAtUtc IS NULL)
            ),
        CONSTRAINT CK_ExportErrors_IdempotencyKey_Hex
            CHECK (IdempotencyKey IS NULL
                OR (LEN(IdempotencyKey) = 64
                    AND IdempotencyKey NOT LIKE '%[^0-9a-f]%' COLLATE Latin1_General_BIN2))
    );
END
GO

/* =========================================================================
 * 6. dbo.ExportCheckpoints — monotonic enumeration cursor per (job, partition).
 *    Only one row per (ExportJobId, PartitionKey) has Status='Active'; older
 *    rows become 'Superseded' so we retain resumption history for forensics.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.ExportCheckpoints', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExportCheckpoints
    (
        ExportCheckpointId       BIGINT           IDENTITY(1,1) NOT NULL,
        ExportJobId              BIGINT           NOT NULL,
        PartitionKey             NVARCHAR(100)    NOT NULL,
        LastDocumentFilePartId   BIGINT           NOT NULL,
        LastVersionPartId        BIGINT           NOT NULL,
        DocumentsProcessedInPartition BIGINT      NULL,
        CheckpointAtUtc          DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportCheckpoints_CheckpointAtUtc DEFAULT (SYSUTCDATETIME()),
        Status                   NVARCHAR(32)     NOT NULL
                                                  CONSTRAINT DF_ExportCheckpoints_Status DEFAULT (N'Active'),
        CreatedDate              DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportCheckpoints_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy                NVARCHAR(128)    NOT NULL
                                                  CONSTRAINT DF_ExportCheckpoints_CreatedBy DEFAULT (SUSER_SNAME()),
        ModifiedDate             DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportCheckpoints_ModifiedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedBy               NVARCHAR(128)    NOT NULL
                                                  CONSTRAINT DF_ExportCheckpoints_ModifiedBy DEFAULT (SUSER_SNAME()),
        RowVersion               ROWVERSION       NOT NULL,

        CONSTRAINT PK_ExportCheckpoints
            PRIMARY KEY CLUSTERED (ExportCheckpointId),
        CONSTRAINT FK_ExportCheckpoints_Job
            FOREIGN KEY (ExportJobId) REFERENCES dbo.ExportJobs (ExportJobId)
            ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT CK_ExportCheckpoints_Status
            CHECK (Status IN (N'Active', N'Superseded', N'Rolled Back', N'Archived'))
    );

    /* Only one Active checkpoint per (Job, Partition). Filtered unique index
       enforces this without collapsing the historical rows. */
    CREATE UNIQUE INDEX UX_ExportCheckpoints_Active_JobPartition
        ON dbo.ExportCheckpoints (ExportJobId, PartitionKey)
        WHERE Status = N'Active';
END
GO

/* =========================================================================
 * 7. dbo.ExportAudit — audit trail: every material state change is recorded
 *    here (job created/started/completed/failed, worker registered/stopped,
 *    checkpoint saved, error resolved, config changed, archive events).
 * ========================================================================= */
IF OBJECT_ID(N'dbo.ExportAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExportAudit
    (
        ExportAuditId            BIGINT           IDENTITY(1,1) NOT NULL,
        ExportJobId              BIGINT           NULL,
        EntityType               NVARCHAR(50)     NOT NULL,   -- 'ExportJobs' | 'ExportWorkers' | ...
        EntityId                 BIGINT           NOT NULL,
        AuditAction              NVARCHAR(50)     NOT NULL,
        PreviousStatus           NVARCHAR(32)     NULL,
        NewStatus                NVARCHAR(32)     NULL,
        ActionDetails            NVARCHAR(MAX)    NULL,        -- JSON
        ActorName                NVARCHAR(200)    NOT NULL
                                                  CONSTRAINT DF_ExportAudit_ActorName DEFAULT (SUSER_SNAME()),
        ActorType                NVARCHAR(32)     NOT NULL
                                                  CONSTRAINT DF_ExportAudit_ActorType DEFAULT (N'System'),
        OccurredAtUtc            DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportAudit_OccurredAtUtc DEFAULT (SYSUTCDATETIME()),
        Status                   NVARCHAR(32)     NOT NULL
                                                  CONSTRAINT DF_ExportAudit_Status DEFAULT (N'Recorded'),
        CreatedDate              DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportAudit_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy                NVARCHAR(128)    NOT NULL
                                                  CONSTRAINT DF_ExportAudit_CreatedBy DEFAULT (SUSER_SNAME()),
        ModifiedDate             DATETIME2(3)     NOT NULL
                                                  CONSTRAINT DF_ExportAudit_ModifiedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedBy               NVARCHAR(128)    NOT NULL
                                                  CONSTRAINT DF_ExportAudit_ModifiedBy DEFAULT (SUSER_SNAME()),
        RowVersion               ROWVERSION       NOT NULL,

        CONSTRAINT PK_ExportAudit
            PRIMARY KEY CLUSTERED (ExportAuditId),
        CONSTRAINT FK_ExportAudit_Job
            FOREIGN KEY (ExportJobId) REFERENCES dbo.ExportJobs (ExportJobId)
            ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT CK_ExportAudit_EntityType
            CHECK (EntityType IN (
                N'ExportJobs', N'ExportWorkers', N'ExportProgress',
                N'ExportMetrics', N'ExportErrors', N'ExportCheckpoints',
                N'Configuration')),
        CONSTRAINT CK_ExportAudit_Action
            CHECK (AuditAction IN (
                N'Created', N'Updated', N'Deleted',
                N'Started', N'Completed', N'Failed', N'Cancelled', N'Paused', N'Resumed',
                N'Registered', N'HeartbeatReceived', N'Stalled', N'Stopped',
                N'CheckpointSaved', N'CheckpointRolledBack',
                N'ErrorRaised', N'ErrorResolved',
                N'Archived', N'Purged', N'Restored',
                N'StatusChanged', N'ConfigurationChanged')),
        CONSTRAINT CK_ExportAudit_ActorType
            CHECK (ActorType IN (N'System', N'Worker', N'Scheduler', N'User', N'Service')),
        CONSTRAINT CK_ExportAudit_Status
            CHECK (Status IN (N'Recorded', N'Archived')),
        CONSTRAINT CK_ExportAudit_ActionDetails_IsJson
            CHECK (ActionDetails IS NULL OR ISJSON(ActionDetails) = 1)
    );
END
GO

/****************************************************************************
 * Archive shadow tables — same shape, on ArchiveFG. Populated by ops sprocs;
 * never written to by the exporter.
 ****************************************************************************/
IF OBJECT_ID(N'archive.ExportJobs', N'U') IS NULL
BEGIN
    CREATE TABLE archive.ExportJobs
    (
        ExportJobId              BIGINT           NOT NULL,
        JobName                  NVARCHAR(200)    NOT NULL,
        SourceServer             NVARCHAR(256)    NOT NULL,
        SourceDatabase           NVARCHAR(256)    NOT NULL,
        PartitionKey             NVARCHAR(100)    NOT NULL,
        TotalDocumentsExpected   BIGINT           NULL,
        StartedAtUtc             DATETIME2(3)     NULL,
        CompletedAtUtc           DATETIME2(3)     NULL,
        CancellationReason       NVARCHAR(2000)   NULL,
        Status                   NVARCHAR(32)     NOT NULL,
        CreatedDate              DATETIME2(3)     NOT NULL,
        CreatedBy                NVARCHAR(128)    NOT NULL,
        ModifiedDate             DATETIME2(3)     NOT NULL,
        ModifiedBy               NVARCHAR(128)    NOT NULL,
        ArchivedAtUtc            DATETIME2(3)     NOT NULL DEFAULT (SYSUTCDATETIME()),
        ArchivedBy               NVARCHAR(128)    NOT NULL DEFAULT (SUSER_SNAME()),
        CONSTRAINT PK_ArchiveExportJobs PRIMARY KEY CLUSTERED (ExportJobId)
    ) ON [ArchiveFG];
END
GO

IF OBJECT_ID(N'archive.ExportWorkers', N'U') IS NULL
BEGIN
    CREATE TABLE archive.ExportWorkers
    (
        ExportWorkerId           BIGINT           NOT NULL,
        ExportJobId              BIGINT           NOT NULL,
        WorkerName               NVARCHAR(200)    NOT NULL,
        MachineName              NVARCHAR(200)    NOT NULL,
        ProcessId                INT              NULL,
        AssignedPartition        NVARCHAR(100)    NOT NULL,
        Concurrency              INT              NOT NULL,
        StartedAtUtc             DATETIME2(3)     NULL,
        LastHeartbeatUtc         DATETIME2(3)     NULL,
        StoppedAtUtc             DATETIME2(3)     NULL,
        Status                   NVARCHAR(32)     NOT NULL,
        CreatedDate              DATETIME2(3)     NOT NULL,
        CreatedBy                NVARCHAR(128)    NOT NULL,
        ModifiedDate             DATETIME2(3)     NOT NULL,
        ModifiedBy               NVARCHAR(128)    NOT NULL,
        ArchivedAtUtc            DATETIME2(3)     NOT NULL DEFAULT (SYSUTCDATETIME()),
        ArchivedBy               NVARCHAR(128)    NOT NULL DEFAULT (SUSER_SNAME()),
        CONSTRAINT PK_ArchiveExportWorkers PRIMARY KEY CLUSTERED (ExportWorkerId)
    ) ON [ArchiveFG];
END
GO

IF OBJECT_ID(N'archive.ExportProgress', N'U') IS NULL
BEGIN
    CREATE TABLE archive.ExportProgress
    (
        ExportProgressId         BIGINT           NOT NULL,
        ExportJobId              BIGINT           NOT NULL,
        ExportWorkerId           BIGINT           NULL,
        SnapshotAtUtc            DATETIME2(3)     NOT NULL,
        TotalRecorded            BIGINT           NOT NULL,
        TotalSucceeded           BIGINT           NOT NULL,
        TotalFailed              BIGINT           NOT NULL,
        TotalSkipped             BIGINT           NOT NULL,
        TotalBytesWritten        BIGINT           NOT NULL,
        DocumentsPerSecond       DECIMAL(18,4)    NULL,
        MebibytesPerSecond       DECIMAL(18,4)    NULL,
        LastDocumentFilePartId   BIGINT           NULL,
        LastVersionPartId        BIGINT           NULL,
        Status                   NVARCHAR(32)     NOT NULL,
        CreatedDate              DATETIME2(3)     NOT NULL,
        CreatedBy                NVARCHAR(128)    NOT NULL,
        ArchivedAtUtc            DATETIME2(3)     NOT NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ArchiveExportProgress PRIMARY KEY CLUSTERED (ExportProgressId)
    ) ON [ArchiveFG];
END
GO

IF OBJECT_ID(N'archive.ExportMetrics', N'U') IS NULL
BEGIN
    CREATE TABLE archive.ExportMetrics
    (
        ExportMetricId           BIGINT           NOT NULL,
        ExportJobId              BIGINT           NOT NULL,
        ExportWorkerId           BIGINT           NULL,
        MetricName               NVARCHAR(200)    NOT NULL,
        MetricValue              FLOAT            NOT NULL,
        MetricUnit               NVARCHAR(50)     NOT NULL,
        Tags                     NVARCHAR(2000)   NULL,
        CapturedAtUtc            DATETIME2(3)     NOT NULL,
        Status                   NVARCHAR(32)     NOT NULL,
        CreatedDate              DATETIME2(3)     NOT NULL,
        ArchivedAtUtc            DATETIME2(3)     NOT NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ArchiveExportMetrics PRIMARY KEY CLUSTERED (ExportMetricId)
    ) ON [ArchiveFG];
END
GO

IF OBJECT_ID(N'archive.ExportErrors', N'U') IS NULL
BEGIN
    CREATE TABLE archive.ExportErrors
    (
        ExportErrorId            BIGINT           NOT NULL,
        ExportJobId              BIGINT           NOT NULL,
        ExportWorkerId           BIGINT           NULL,
        DocumentFilePartId       BIGINT           NULL,
        VersionPartId            BIGINT           NULL,
        DataFileVersionId        BIGINT           NULL,
        IdempotencyKey           CHAR(64)         NULL,
        ErrorSeverity            NVARCHAR(16)     NOT NULL,
        ErrorCategory            NVARCHAR(32)     NOT NULL,
        ErrorSource              NVARCHAR(200)    NOT NULL,
        ExceptionType            NVARCHAR(400)    NULL,
        ErrorMessage             NVARCHAR(4000)   NOT NULL,
        StackTrace               NVARCHAR(MAX)    NULL,
        AttemptNumber            INT              NOT NULL,
        OccurredAtUtc            DATETIME2(3)     NOT NULL,
        ResolvedAtUtc            DATETIME2(3)     NULL,
        ResolvedBy               NVARCHAR(128)    NULL,
        ResolutionNotes          NVARCHAR(2000)   NULL,
        Status                   NVARCHAR(32)     NOT NULL,
        CreatedDate              DATETIME2(3)     NOT NULL,
        ArchivedAtUtc            DATETIME2(3)     NOT NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ArchiveExportErrors PRIMARY KEY CLUSTERED (ExportErrorId)
    ) ON [ArchiveFG];
END
GO

IF OBJECT_ID(N'archive.ExportCheckpoints', N'U') IS NULL
BEGIN
    CREATE TABLE archive.ExportCheckpoints
    (
        ExportCheckpointId       BIGINT           NOT NULL,
        ExportJobId              BIGINT           NOT NULL,
        PartitionKey             NVARCHAR(100)    NOT NULL,
        LastDocumentFilePartId   BIGINT           NOT NULL,
        LastVersionPartId        BIGINT           NOT NULL,
        DocumentsProcessedInPartition BIGINT      NULL,
        CheckpointAtUtc          DATETIME2(3)     NOT NULL,
        Status                   NVARCHAR(32)     NOT NULL,
        CreatedDate              DATETIME2(3)     NOT NULL,
        ArchivedAtUtc            DATETIME2(3)     NOT NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ArchiveExportCheckpoints PRIMARY KEY CLUSTERED (ExportCheckpointId)
    ) ON [ArchiveFG];
END
GO

IF OBJECT_ID(N'archive.ExportAudit', N'U') IS NULL
BEGIN
    CREATE TABLE archive.ExportAudit
    (
        ExportAuditId            BIGINT           NOT NULL,
        ExportJobId              BIGINT           NULL,
        EntityType               NVARCHAR(50)     NOT NULL,
        EntityId                 BIGINT           NOT NULL,
        AuditAction              NVARCHAR(50)     NOT NULL,
        PreviousStatus           NVARCHAR(32)     NULL,
        NewStatus                NVARCHAR(32)     NULL,
        ActionDetails            NVARCHAR(MAX)    NULL,
        ActorName                NVARCHAR(200)    NOT NULL,
        ActorType                NVARCHAR(32)     NOT NULL,
        OccurredAtUtc            DATETIME2(3)     NOT NULL,
        Status                   NVARCHAR(32)     NOT NULL,
        CreatedDate              DATETIME2(3)     NOT NULL,
        ArchivedAtUtc            DATETIME2(3)     NOT NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ArchiveExportAudit PRIMARY KEY CLUSTERED (ExportAuditId)
    ) ON [ArchiveFG];
END
GO
