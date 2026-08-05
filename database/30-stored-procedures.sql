/****************************************************************************
 * File:        30-stored-procedures.sql
 * Purpose:     Public API surface used by the exporter and operators.
 *              All writes to the tracking DB should flow through these
 *              procs — direct DML from application code is discouraged.
 *
 * Conventions:
 *   * Every proc runs under SET XACT_ABORT ON.
 *   * Every proc that mutates state writes an ExportAudit row.
 *   * @ActorName / @ActorType default to SUSER_SNAME() / N'System'.
 *   * Return code 0 = success. Non-zero = error (also raises via THROW).
 ****************************************************************************/

USE [MFilesExportTracking];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* =========================================================================
 * usp_StartExportJob — create a new job row and start it (Status='Running').
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_StartExportJob', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_StartExportJob;
GO
CREATE PROCEDURE dbo.usp_StartExportJob
    @JobName                NVARCHAR(200),
    @SourceServer           NVARCHAR(256),
    @SourceDatabase         NVARCHAR(256),
    @PartitionKey           NVARCHAR(100) = N'default',
    @TotalDocumentsExpected BIGINT        = NULL,
    @ActorName              NVARCHAR(200) = NULL,
    @ActorType              NVARCHAR(32)  = N'System',
    @ExportJobId            BIGINT        OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @ActorName = ISNULL(@ActorName, SUSER_SNAME());

    BEGIN TRAN;

    INSERT INTO dbo.ExportJobs
        (JobName, SourceServer, SourceDatabase, PartitionKey,
         TotalDocumentsExpected, StartedAtUtc, Status,
         CreatedBy, ModifiedBy)
    VALUES
        (@JobName, @SourceServer, @SourceDatabase, @PartitionKey,
         @TotalDocumentsExpected, SYSUTCDATETIME(), N'Running',
         @ActorName, @ActorName);

    SET @ExportJobId = SCOPE_IDENTITY();

    INSERT INTO dbo.ExportAudit
        (ExportJobId, EntityType, EntityId, AuditAction,
         PreviousStatus, NewStatus, ActionDetails, ActorName, ActorType)
    VALUES
        (@ExportJobId, N'ExportJobs', @ExportJobId, N'Started',
         N'Pending', N'Running',
         (SELECT @JobName AS JobName, @PartitionKey AS PartitionKey,
                 @SourceServer AS SourceServer, @SourceDatabase AS SourceDatabase
          FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
         @ActorName, @ActorType);

    COMMIT;
    RETURN 0;
END
GO

/* =========================================================================
 * usp_CompleteExportJob — mark a job as Completed / Failed / Cancelled.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_CompleteExportJob', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_CompleteExportJob;
GO
CREATE PROCEDURE dbo.usp_CompleteExportJob
    @ExportJobId        BIGINT,
    @TerminalStatus     NVARCHAR(32),   -- 'Completed' | 'Failed' | 'Cancelled'
    @Reason             NVARCHAR(2000) = NULL,
    @ActorName          NVARCHAR(200) = NULL,
    @ActorType          NVARCHAR(32)  = N'System'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @TerminalStatus NOT IN (N'Completed', N'Failed', N'Cancelled')
    BEGIN
        THROW 51001, N'@TerminalStatus must be one of Completed | Failed | Cancelled.', 1;
    END

    SET @ActorName = ISNULL(@ActorName, SUSER_SNAME());

    DECLARE @previousStatus NVARCHAR(32);

    BEGIN TRAN;

    SELECT @previousStatus = Status
    FROM dbo.ExportJobs WITH (UPDLOCK, HOLDLOCK)
    WHERE ExportJobId = @ExportJobId;

    IF @previousStatus IS NULL
    BEGIN
        ROLLBACK;
        THROW 51002, N'Export job not found.', 1;
    END

    UPDATE dbo.ExportJobs
    SET Status              = @TerminalStatus,
        CompletedAtUtc      = SYSUTCDATETIME(),
        CancellationReason  = CASE WHEN @TerminalStatus = N'Cancelled' THEN @Reason ELSE CancellationReason END,
        ModifiedDate        = SYSUTCDATETIME(),
        ModifiedBy          = @ActorName
    WHERE ExportJobId = @ExportJobId;

    INSERT INTO dbo.ExportAudit
        (ExportJobId, EntityType, EntityId, AuditAction,
         PreviousStatus, NewStatus, ActionDetails, ActorName, ActorType)
    VALUES
        (@ExportJobId, N'ExportJobs', @ExportJobId, @TerminalStatus,
         @previousStatus, @TerminalStatus,
         CASE WHEN @Reason IS NULL THEN NULL
              ELSE (SELECT @Reason AS Reason FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
         END,
         @ActorName, @ActorType);

    COMMIT;
    RETURN 0;
END
GO

/* =========================================================================
 * usp_RegisterExportWorker
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_RegisterExportWorker', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_RegisterExportWorker;
GO
CREATE PROCEDURE dbo.usp_RegisterExportWorker
    @ExportJobId         BIGINT,
    @WorkerName          NVARCHAR(200),
    @MachineName         NVARCHAR(200),
    @ProcessId           INT             = NULL,
    @AssignedPartition   NVARCHAR(100),
    @Concurrency         INT             = 1,
    @ActorName           NVARCHAR(200)   = NULL,
    @ActorType           NVARCHAR(32)    = N'Worker',
    @ExportWorkerId      BIGINT          OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @ActorName = ISNULL(@ActorName, SUSER_SNAME());

    BEGIN TRAN;

    -- Upsert: same (Job, Partition, Worker, Machine) reuses row.
    SELECT @ExportWorkerId = ExportWorkerId
    FROM dbo.ExportWorkers WITH (UPDLOCK, HOLDLOCK)
    WHERE ExportJobId = @ExportJobId
      AND AssignedPartition = @AssignedPartition
      AND WorkerName = @WorkerName
      AND MachineName = @MachineName;

    IF @ExportWorkerId IS NULL
    BEGIN
        INSERT INTO dbo.ExportWorkers
            (ExportJobId, WorkerName, MachineName, ProcessId,
             AssignedPartition, Concurrency,
             StartedAtUtc, LastHeartbeatUtc, Status,
             CreatedBy, ModifiedBy)
        VALUES
            (@ExportJobId, @WorkerName, @MachineName, @ProcessId,
             @AssignedPartition, @Concurrency,
             SYSUTCDATETIME(), SYSUTCDATETIME(), N'Active',
             @ActorName, @ActorName);

        SET @ExportWorkerId = SCOPE_IDENTITY();
    END
    ELSE
    BEGIN
        UPDATE dbo.ExportWorkers
        SET ProcessId        = @ProcessId,
            Concurrency      = @Concurrency,
            StartedAtUtc     = SYSUTCDATETIME(),
            LastHeartbeatUtc = SYSUTCDATETIME(),
            StoppedAtUtc     = NULL,
            Status           = N'Active',
            ModifiedDate     = SYSUTCDATETIME(),
            ModifiedBy       = @ActorName
        WHERE ExportWorkerId = @ExportWorkerId;
    END

    INSERT INTO dbo.ExportAudit
        (ExportJobId, EntityType, EntityId, AuditAction,
         PreviousStatus, NewStatus, ActionDetails, ActorName, ActorType)
    VALUES
        (@ExportJobId, N'ExportWorkers', @ExportWorkerId, N'Registered',
         NULL, N'Active',
         (SELECT @WorkerName AS WorkerName, @MachineName AS MachineName,
                 @AssignedPartition AS Partition, @Concurrency AS Concurrency
          FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
         @ActorName, @ActorType);

    COMMIT;
    RETURN 0;
END
GO

/* =========================================================================
 * usp_HeartbeatExportWorker
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_HeartbeatExportWorker', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_HeartbeatExportWorker;
GO
CREATE PROCEDURE dbo.usp_HeartbeatExportWorker
    @ExportWorkerId  BIGINT,
    @NewStatus       NVARCHAR(32) = N'Active'
AS
BEGIN
    SET NOCOUNT ON;
    IF @NewStatus NOT IN (N'Active', N'Idle', N'Stalled')
    BEGIN
        THROW 51003, N'@NewStatus must be Active | Idle | Stalled.', 1;
    END

    UPDATE dbo.ExportWorkers
    SET LastHeartbeatUtc = SYSUTCDATETIME(),
        Status           = @NewStatus,
        ModifiedDate     = SYSUTCDATETIME(),
        ModifiedBy       = SUSER_SNAME()
    WHERE ExportWorkerId = @ExportWorkerId;

    RETURN 0;
END
GO

/* =========================================================================
 * usp_StopExportWorker
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_StopExportWorker', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_StopExportWorker;
GO
CREATE PROCEDURE dbo.usp_StopExportWorker
    @ExportWorkerId  BIGINT,
    @Reason          NVARCHAR(1000) = NULL,
    @ActorName       NVARCHAR(200)  = NULL,
    @ActorType       NVARCHAR(32)   = N'Worker'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @ActorName = ISNULL(@ActorName, SUSER_SNAME());

    DECLARE @jobId BIGINT, @prev NVARCHAR(32);

    BEGIN TRAN;

    SELECT @jobId = ExportJobId, @prev = Status
    FROM dbo.ExportWorkers WITH (UPDLOCK, HOLDLOCK)
    WHERE ExportWorkerId = @ExportWorkerId;

    IF @jobId IS NULL
    BEGIN
        ROLLBACK;
        THROW 51004, N'Export worker not found.', 1;
    END

    UPDATE dbo.ExportWorkers
    SET Status       = N'Stopped',
        StoppedAtUtc = SYSUTCDATETIME(),
        ModifiedDate = SYSUTCDATETIME(),
        ModifiedBy   = @ActorName
    WHERE ExportWorkerId = @ExportWorkerId;

    INSERT INTO dbo.ExportAudit
        (ExportJobId, EntityType, EntityId, AuditAction,
         PreviousStatus, NewStatus, ActionDetails, ActorName, ActorType)
    VALUES
        (@jobId, N'ExportWorkers', @ExportWorkerId, N'Stopped',
         @prev, N'Stopped',
         CASE WHEN @Reason IS NULL THEN NULL
              ELSE (SELECT @Reason AS Reason FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
         END,
         @ActorName, @ActorType);

    COMMIT;
    RETURN 0;
END
GO

/* =========================================================================
 * usp_MarkStalledWorkers — ops sweep. Marks workers whose LastHeartbeatUtc
 * is older than @StaleAfterSeconds as 'Stalled' and audits the change.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_MarkStalledWorkers', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_MarkStalledWorkers;
GO
CREATE PROCEDURE dbo.usp_MarkStalledWorkers
    @StaleAfterSeconds INT = 120
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @threshold DATETIME2(3) = DATEADD(SECOND, -@StaleAfterSeconds, SYSUTCDATETIME());

    DECLARE @changed TABLE
    (
        ExportWorkerId BIGINT NOT NULL,
        ExportJobId    BIGINT NOT NULL,
        PrevStatus     NVARCHAR(32) NOT NULL
    );

    BEGIN TRAN;

    UPDATE w
    SET Status       = N'Stalled',
        ModifiedDate = SYSUTCDATETIME(),
        ModifiedBy   = SUSER_SNAME()
    OUTPUT inserted.ExportWorkerId, inserted.ExportJobId, deleted.Status
        INTO @changed (ExportWorkerId, ExportJobId, PrevStatus)
    FROM dbo.ExportWorkers AS w
    WHERE w.Status IN (N'Active', N'Idle')
      AND w.LastHeartbeatUtc < @threshold;

    INSERT INTO dbo.ExportAudit
        (ExportJobId, EntityType, EntityId, AuditAction,
         PreviousStatus, NewStatus, ActionDetails, ActorName, ActorType)
    SELECT ExportJobId, N'ExportWorkers', ExportWorkerId, N'Stalled',
           PrevStatus, N'Stalled',
           (SELECT @StaleAfterSeconds AS StaleAfterSeconds
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
           SUSER_SNAME(), N'System'
    FROM @changed;

    COMMIT;
    RETURN 0;
END
GO

/* =========================================================================
 * usp_RecordExportProgress — append a new progress snapshot.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_RecordExportProgress', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_RecordExportProgress;
GO
CREATE PROCEDURE dbo.usp_RecordExportProgress
    @ExportJobId            BIGINT,
    @ExportWorkerId         BIGINT       = NULL,
    @TotalRecorded          BIGINT,
    @TotalSucceeded         BIGINT,
    @TotalFailed            BIGINT,
    @TotalSkipped           BIGINT,
    @TotalBytesWritten      BIGINT,
    @DocumentsPerSecond     DECIMAL(18,4) = NULL,
    @MebibytesPerSecond     DECIMAL(18,4) = NULL,
    @LastDocumentFilePartId BIGINT       = NULL,
    @LastVersionPartId      BIGINT       = NULL,
    @ActorName              NVARCHAR(200) = NULL,
    @ExportProgressId       BIGINT       OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SET @ActorName = ISNULL(@ActorName, SUSER_SNAME());

    INSERT INTO dbo.ExportProgress
        (ExportJobId, ExportWorkerId,
         TotalRecorded, TotalSucceeded, TotalFailed, TotalSkipped, TotalBytesWritten,
         DocumentsPerSecond, MebibytesPerSecond,
         LastDocumentFilePartId, LastVersionPartId,
         Status, CreatedBy, ModifiedBy)
    VALUES
        (@ExportJobId, @ExportWorkerId,
         @TotalRecorded, @TotalSucceeded, @TotalFailed, @TotalSkipped, @TotalBytesWritten,
         @DocumentsPerSecond, @MebibytesPerSecond,
         @LastDocumentFilePartId, @LastVersionPartId,
         N'Snapshot', @ActorName, @ActorName);

    SET @ExportProgressId = SCOPE_IDENTITY();
    RETURN 0;
END
GO

/* =========================================================================
 * usp_RecordExportMetric — append a metric sample.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_RecordExportMetric', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_RecordExportMetric;
GO
CREATE PROCEDURE dbo.usp_RecordExportMetric
    @ExportJobId    BIGINT,
    @ExportWorkerId BIGINT        = NULL,
    @MetricName     NVARCHAR(200),
    @MetricValue    FLOAT,
    @MetricUnit     NVARCHAR(50)  = N'',
    @Tags           NVARCHAR(2000)= NULL,
    @ActorName      NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SET @ActorName = ISNULL(@ActorName, SUSER_SNAME());

    INSERT INTO dbo.ExportMetrics
        (ExportJobId, ExportWorkerId, MetricName, MetricValue, MetricUnit, Tags,
         Status, CreatedBy, ModifiedBy)
    VALUES
        (@ExportJobId, @ExportWorkerId, @MetricName, @MetricValue, @MetricUnit, @Tags,
         N'Live', @ActorName, @ActorName);

    RETURN 0;
END
GO

/* =========================================================================
 * usp_LogExportError — record a new error and mirror to audit.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_LogExportError', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_LogExportError;
GO
CREATE PROCEDURE dbo.usp_LogExportError
    @ExportJobId          BIGINT,
    @ExportWorkerId       BIGINT       = NULL,
    @DocumentFilePartId   BIGINT       = NULL,
    @VersionPartId        BIGINT       = NULL,
    @DataFileVersionId    BIGINT       = NULL,
    @IdempotencyKey       CHAR(64)     = NULL,
    @ErrorSeverity        NVARCHAR(16) = N'Error',
    @ErrorCategory        NVARCHAR(32) = N'Unknown',
    @ErrorSource          NVARCHAR(200),
    @ExceptionType        NVARCHAR(400)= NULL,
    @ErrorMessage         NVARCHAR(4000),
    @StackTrace           NVARCHAR(MAX)= NULL,
    @AttemptNumber        INT          = 1,
    @ActorName            NVARCHAR(200)= NULL,
    @ActorType            NVARCHAR(32) = N'Worker',
    @ExportErrorId        BIGINT       OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @ActorName = ISNULL(@ActorName, SUSER_SNAME());

    BEGIN TRAN;

    INSERT INTO dbo.ExportErrors
        (ExportJobId, ExportWorkerId,
         DocumentFilePartId, VersionPartId, DataFileVersionId, IdempotencyKey,
         ErrorSeverity, ErrorCategory, ErrorSource,
         ExceptionType, ErrorMessage, StackTrace,
         AttemptNumber, Status, CreatedBy, ModifiedBy)
    VALUES
        (@ExportJobId, @ExportWorkerId,
         @DocumentFilePartId, @VersionPartId, @DataFileVersionId, @IdempotencyKey,
         @ErrorSeverity, @ErrorCategory, @ErrorSource,
         @ExceptionType, @ErrorMessage, @StackTrace,
         @AttemptNumber, N'New', @ActorName, @ActorName);

    SET @ExportErrorId = SCOPE_IDENTITY();

    INSERT INTO dbo.ExportAudit
        (ExportJobId, EntityType, EntityId, AuditAction,
         PreviousStatus, NewStatus, ActionDetails, ActorName, ActorType)
    VALUES
        (@ExportJobId, N'ExportErrors', @ExportErrorId, N'ErrorRaised',
         NULL, N'New',
         (SELECT @ErrorSeverity AS Severity, @ErrorCategory AS Category,
                 @ErrorSource AS Source, @ExceptionType AS ExceptionType
          FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
         @ActorName, @ActorType);

    COMMIT;
    RETURN 0;
END
GO

/* =========================================================================
 * usp_ResolveExportError — mark an error as resolved / ignored.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_ResolveExportError', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_ResolveExportError;
GO
CREATE PROCEDURE dbo.usp_ResolveExportError
    @ExportErrorId      BIGINT,
    @NewStatus          NVARCHAR(32) = N'Resolved',  -- 'Resolved' | 'Ignored'
    @ResolutionNotes    NVARCHAR(2000) = NULL,
    @ActorName          NVARCHAR(200) = NULL,
    @ActorType          NVARCHAR(32)  = N'User'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @NewStatus NOT IN (N'Resolved', N'Ignored')
    BEGIN
        THROW 51005, N'@NewStatus must be Resolved | Ignored.', 1;
    END

    SET @ActorName = ISNULL(@ActorName, SUSER_SNAME());

    DECLARE @jobId BIGINT, @prev NVARCHAR(32);

    BEGIN TRAN;

    SELECT @jobId = ExportJobId, @prev = Status
    FROM dbo.ExportErrors WITH (UPDLOCK, HOLDLOCK)
    WHERE ExportErrorId = @ExportErrorId;

    IF @jobId IS NULL
    BEGIN
        ROLLBACK;
        THROW 51006, N'Export error not found.', 1;
    END

    UPDATE dbo.ExportErrors
    SET Status          = @NewStatus,
        ResolvedAtUtc   = SYSUTCDATETIME(),
        ResolvedBy      = @ActorName,
        ResolutionNotes = @ResolutionNotes,
        ModifiedDate    = SYSUTCDATETIME(),
        ModifiedBy      = @ActorName
    WHERE ExportErrorId = @ExportErrorId;

    INSERT INTO dbo.ExportAudit
        (ExportJobId, EntityType, EntityId, AuditAction,
         PreviousStatus, NewStatus, ActionDetails, ActorName, ActorType)
    VALUES
        (@jobId, N'ExportErrors', @ExportErrorId, N'ErrorResolved',
         @prev, @NewStatus,
         CASE WHEN @ResolutionNotes IS NULL THEN NULL
              ELSE (SELECT @ResolutionNotes AS ResolutionNotes FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
         END,
         @ActorName, @ActorType);

    COMMIT;
    RETURN 0;
END
GO

/* =========================================================================
 * usp_SaveExportCheckpoint — monotonic upsert. Supersedes the previous
 * Active row and inserts a new one — atomic within a transaction. If the
 * new candidate is not strictly greater than the current Active row, no
 * change is made and @Advanced returns 0.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_SaveExportCheckpoint', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_SaveExportCheckpoint;
GO
CREATE PROCEDURE dbo.usp_SaveExportCheckpoint
    @ExportJobId                   BIGINT,
    @PartitionKey                  NVARCHAR(100),
    @LastDocumentFilePartId        BIGINT,
    @LastVersionPartId             BIGINT,
    @DocumentsProcessedInPartition BIGINT       = NULL,
    @ActorName                     NVARCHAR(200)= NULL,
    @ActorType                     NVARCHAR(32) = N'Worker',
    @Advanced                      BIT          OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @ActorName = ISNULL(@ActorName, SUSER_SNAME());
    SET @Advanced = 0;

    BEGIN TRAN;

    DECLARE @currentId BIGINT, @currentPart BIGINT, @currentVer BIGINT;

    SELECT TOP (1)
        @currentId   = ExportCheckpointId,
        @currentPart = LastDocumentFilePartId,
        @currentVer  = LastVersionPartId
    FROM dbo.ExportCheckpoints WITH (UPDLOCK, HOLDLOCK)
    WHERE ExportJobId = @ExportJobId
      AND PartitionKey = @PartitionKey
      AND Status = N'Active';

    IF @currentId IS NOT NULL
    BEGIN
        IF (@LastDocumentFilePartId  < @currentPart)
           OR (@LastDocumentFilePartId = @currentPart AND @LastVersionPartId <= @currentVer)
        BEGIN
            -- Candidate not strictly greater — no-op.
            COMMIT;
            RETURN 0;
        END

        UPDATE dbo.ExportCheckpoints
        SET Status       = N'Superseded',
            ModifiedDate = SYSUTCDATETIME(),
            ModifiedBy   = @ActorName
        WHERE ExportCheckpointId = @currentId;
    END

    INSERT INTO dbo.ExportCheckpoints
        (ExportJobId, PartitionKey,
         LastDocumentFilePartId, LastVersionPartId,
         DocumentsProcessedInPartition,
         Status, CreatedBy, ModifiedBy)
    VALUES
        (@ExportJobId, @PartitionKey,
         @LastDocumentFilePartId, @LastVersionPartId,
         @DocumentsProcessedInPartition,
         N'Active', @ActorName, @ActorName);

    DECLARE @newId BIGINT = SCOPE_IDENTITY();
    SET @Advanced = 1;

    INSERT INTO dbo.ExportAudit
        (ExportJobId, EntityType, EntityId, AuditAction,
         PreviousStatus, NewStatus, ActionDetails, ActorName, ActorType)
    VALUES
        (@ExportJobId, N'ExportCheckpoints', @newId, N'CheckpointSaved',
         CASE WHEN @currentId IS NULL THEN NULL ELSE N'Active' END, N'Active',
         (SELECT @PartitionKey AS PartitionKey,
                 @LastDocumentFilePartId AS Part, @LastVersionPartId AS Ver
          FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
         @ActorName, @ActorType);

    COMMIT;
    RETURN 0;
END
GO

/* =========================================================================
 * usp_GetLatestCheckpoint — read the current Active checkpoint (or NULL).
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_GetLatestCheckpoint', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_GetLatestCheckpoint;
GO
CREATE PROCEDURE dbo.usp_GetLatestCheckpoint
    @ExportJobId  BIGINT,
    @PartitionKey NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (1)
        ExportCheckpointId,
        ExportJobId,
        PartitionKey,
        LastDocumentFilePartId,
        LastVersionPartId,
        DocumentsProcessedInPartition,
        CheckpointAtUtc,
        Status
    FROM dbo.ExportCheckpoints
    WHERE ExportJobId = @ExportJobId
      AND PartitionKey = @PartitionKey
      AND Status = N'Active';

    RETURN 0;
END
GO

/* =========================================================================
 * usp_GetLatestProgress — most recent snapshot for a job.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_GetLatestProgress', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_GetLatestProgress;
GO
CREATE PROCEDURE dbo.usp_GetLatestProgress
    @ExportJobId BIGINT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (1)
        ExportProgressId, ExportJobId, ExportWorkerId, SnapshotAtUtc,
        TotalRecorded, TotalSucceeded, TotalFailed, TotalSkipped, TotalBytesWritten,
        DocumentsPerSecond, MebibytesPerSecond,
        LastDocumentFilePartId, LastVersionPartId, Status
    FROM dbo.ExportProgress
    WHERE ExportJobId = @ExportJobId
    ORDER BY SnapshotAtUtc DESC, ExportProgressId DESC;

    RETURN 0;
END
GO
