/****************************************************************************
 * File:        71-work-claiming-procs.sql
 * Purpose:     The five stored procedures that comprise the distributed
 *              work-claiming engine. Each one is a single atomic statement
 *              (or transaction), leaning on SQL Server's row-locking to
 *              guarantee at-most-once semantics.
 *
 * The procs:
 *   usp_EnqueueWorkItems          - producer inserts new work
 *   usp_ClaimWorkItems            - workers claim next N items atomically
 *   usp_RenewWorkItemLease        - workers extend an active claim
 *   usp_CompleteWorkItem          - worker marks a claim Completed
 *   usp_FailWorkItem              - worker marks a claim Failed (retry-able)
 *   usp_ReclaimExpiredLeases      - background sweep returns expired claims
 ****************************************************************************/

USE [MFilesExportTracking];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* =========================================================================
 * usp_EnqueueWorkItems — TVP-driven bulk enqueue.
 *   Uses MERGE-style INSERT ... WHERE NOT EXISTS so re-running the
 *   enumeration for the same job never inserts a duplicate row (the
 *   UNIQUE constraint on (ExportJobId, IdempotencyKey) also enforces this,
 *   but the WHERE NOT EXISTS keeps failures out of the failure log).
 * ========================================================================= */

IF TYPE_ID(N'dbo.udt_ExportWorkItemBatch') IS NULL
BEGIN
    CREATE TYPE dbo.udt_ExportWorkItemBatch AS TABLE
    (
        IdempotencyKey      CHAR(64) NOT NULL,
        DocumentFilePartId  BIGINT NOT NULL,
        VersionPartId       BIGINT NOT NULL,
        DataFileVersionId   BIGINT NOT NULL,
        Priority            INT NOT NULL,
        MaxAttempts         INT NOT NULL
    );
END
GO

IF OBJECT_ID(N'dbo.usp_EnqueueWorkItems', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_EnqueueWorkItems;
GO
CREATE PROCEDURE dbo.usp_EnqueueWorkItems
    @ExportJobId  BIGINT,
    @Items        dbo.udt_ExportWorkItemBatch READONLY,
    @Enqueued     INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.ExportWorkItems
        (ExportJobId, IdempotencyKey,
         DocumentFilePartId, VersionPartId, DataFileVersionId,
         Priority, MaxAttempts,
         Status, NextEligibleAtUtc)
    SELECT
        @ExportJobId, i.IdempotencyKey,
        i.DocumentFilePartId, i.VersionPartId, i.DataFileVersionId,
        i.Priority, i.MaxAttempts,
        N'Available', SYSUTCDATETIME()
    FROM @Items AS i
    WHERE NOT EXISTS
    (
        SELECT 1 FROM dbo.ExportWorkItems x
        WHERE x.ExportJobId = @ExportJobId
          AND x.IdempotencyKey = i.IdempotencyKey
    );

    SET @Enqueued = @@ROWCOUNT;
    RETURN 0;
END
GO

/* =========================================================================
 * usp_ClaimWorkItems — the heart of the engine.
 *
 *   Atomic UPDATE-with-OUTPUT that:
 *     * Picks up to @BatchSize eligible rows (Available AND not-yet-due
 *       past a retry backoff AND attempts left)
 *     * Uses READPAST + ROWLOCK + UPDLOCK so parallel workers never see
 *       the same row (concurrent claimers pass over each other's locked rows)
 *     * Stamps a fresh ClaimedByToken (UNIQUEIDENTIFIER) — the fencing
 *       token that every subsequent complete/fail/renew must present.
 *     * Sets a lease deadline ClaimExpiresAtUtc = now + @LeaseDurationSec.
 *
 *   Returns the claimed items to the caller via OUTPUT so the whole thing
 *   is a single round-trip.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_ClaimWorkItems', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_ClaimWorkItems;
GO
CREATE PROCEDURE dbo.usp_ClaimWorkItems
    @ExportJobId      BIGINT,
    @WorkerId         BIGINT,
    @BatchSize        INT,
    @LeaseDurationSec INT = 300
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(3)         = SYSUTCDATETIME();
    DECLARE @expires DATETIME2(3)     = DATEADD(SECOND, @LeaseDurationSec, @now);
    DECLARE @claimToken UNIQUEIDENTIFIER = NEWID();

    /* The single atomic statement. Only the UPDATE modifies state — no
       intermediate SELECT that could race with a concurrent claimer. */
    UPDATE TOP (@BatchSize) w
    SET Status              = N'Claimed',
        ClaimedByWorkerId   = @WorkerId,
        ClaimedByToken      = @claimToken,
        ClaimedAtUtc        = @now,
        ClaimExpiresAtUtc   = @expires,
        AttemptCount        = w.AttemptCount + 1,
        ModifiedDate        = @now,
        ModifiedBy          = SUSER_SNAME()
    OUTPUT
        inserted.WorkItemId,
        inserted.IdempotencyKey,
        inserted.DocumentFilePartId,
        inserted.VersionPartId,
        inserted.DataFileVersionId,
        inserted.AttemptCount,
        inserted.MaxAttempts,
        inserted.ClaimedByToken,
        inserted.ClaimExpiresAtUtc
    FROM dbo.ExportWorkItems AS w WITH (READPAST, ROWLOCK, UPDLOCK)
    WHERE w.ExportJobId         = @ExportJobId
      AND w.Status              = N'Available'
      AND w.NextEligibleAtUtc  <= @now
      AND w.AttemptCount        < w.MaxAttempts;

    RETURN 0;
END
GO

/* =========================================================================
 * usp_RenewWorkItemLease — worker extends its lease for long-running items.
 *
 *   The row is only updated when the current ClaimedByToken matches the
 *   worker's token. A stale token yields @Extended = 0, telling the worker
 *   its claim was already stolen and it should abandon.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_RenewWorkItemLease', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_RenewWorkItemLease;
GO
CREATE PROCEDURE dbo.usp_RenewWorkItemLease
    @WorkItemId       BIGINT,
    @ClaimToken       UNIQUEIDENTIFIER,
    @ExtendBySec      INT,
    @Extended         BIT OUTPUT,
    @NewExpiresAtUtc  DATETIME2(3) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
    SET @NewExpiresAtUtc = DATEADD(SECOND, @ExtendBySec, @now);

    UPDATE dbo.ExportWorkItems
    SET ClaimExpiresAtUtc = @NewExpiresAtUtc,
        ModifiedDate      = @now,
        ModifiedBy        = SUSER_SNAME()
    WHERE WorkItemId      = @WorkItemId
      AND ClaimedByToken  = @ClaimToken
      AND Status          = N'Claimed';

    IF @@ROWCOUNT = 1
        SET @Extended = 1;
    ELSE
    BEGIN
        SET @Extended = 0;
        SET @NewExpiresAtUtc = NULL;
    END

    RETURN 0;
END
GO

/* =========================================================================
 * usp_CompleteWorkItem — mark a claim as Completed.
 *
 *   Guarded by the ClaimedByToken match. If a worker's lease expired and
 *   the reaper returned the row to Available, this UPDATE affects zero
 *   rows and returns @Completed = 0 — the worker MUST NOT interpret its
 *   own successful sink write as an authoritative export.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_CompleteWorkItem', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_CompleteWorkItem;
GO
CREATE PROCEDURE dbo.usp_CompleteWorkItem
    @WorkItemId    BIGINT,
    @ClaimToken    UNIQUEIDENTIFIER,
    @OutputPath    NVARCHAR(1024),
    @Checksum      CHAR(64),
    @BytesWritten  BIGINT,
    @Completed     BIT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();

    UPDATE dbo.ExportWorkItems
    SET Status              = N'Completed',
        CompletedAtUtc      = @now,
        OutputPath          = @OutputPath,
        Checksum            = @Checksum,
        BytesWritten        = @BytesWritten,
        ClaimedByWorkerId   = NULL,
        ClaimedByToken      = NULL,
        ClaimExpiresAtUtc   = NULL,
        ModifiedDate        = @now,
        ModifiedBy          = SUSER_SNAME()
    WHERE WorkItemId        = @WorkItemId
      AND ClaimedByToken    = @ClaimToken
      AND Status            = N'Claimed';

    SET @Completed = CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END;
    RETURN 0;
END
GO

/* =========================================================================
 * usp_FailWorkItem — mark a claim as failed.
 *
 *   Two modes:
 *     @IsPermanent = 0 → transient. Row returns to Available (or
 *       DeadLettered if attempts have hit MaxAttempts). NextEligibleAtUtc
 *       advanced by @BackoffSeconds to space out retries.
 *     @IsPermanent = 1 → deterministic. Row becomes Failed (terminal;
 *       operator must decide whether to reset).
 *
 *   Guarded by claim-token match.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_FailWorkItem', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_FailWorkItem;
GO
CREATE PROCEDURE dbo.usp_FailWorkItem
    @WorkItemId       BIGINT,
    @ClaimToken       UNIQUEIDENTIFIER,
    @FailureReason    NVARCHAR(2000),
    @IsPermanent      BIT = 0,
    @BackoffSeconds   INT = 60,
    @Recorded         BIT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();

    IF @IsPermanent = 1
    BEGIN
        UPDATE dbo.ExportWorkItems
        SET Status              = N'Failed',
            LastFailureReason   = @FailureReason,
            LastFailureAtUtc    = @now,
            ClaimedByWorkerId   = NULL,
            ClaimedByToken      = NULL,
            ClaimExpiresAtUtc   = NULL,
            ModifiedDate        = @now,
            ModifiedBy          = SUSER_SNAME()
        WHERE WorkItemId        = @WorkItemId
          AND ClaimedByToken    = @ClaimToken
          AND Status            = N'Claimed';
    END
    ELSE
    BEGIN
        UPDATE dbo.ExportWorkItems
        SET Status =
                CASE WHEN AttemptCount >= MaxAttempts THEN N'DeadLettered' ELSE N'Available' END,
            LastFailureReason   = @FailureReason,
            LastFailureAtUtc    = @now,
            NextEligibleAtUtc   = DATEADD(SECOND, @BackoffSeconds, @now),
            ClaimedByWorkerId   = NULL,
            ClaimedByToken      = NULL,
            ClaimExpiresAtUtc   = NULL,
            ModifiedDate        = @now,
            ModifiedBy          = SUSER_SNAME()
        WHERE WorkItemId        = @WorkItemId
          AND ClaimedByToken    = @ClaimToken
          AND Status            = N'Claimed';
    END

    SET @Recorded = CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END;
    RETURN 0;
END
GO

/* =========================================================================
 * usp_ReclaimExpiredLeases — background sweep.
 *
 *   Scans the filtered index IX_ExportWorkItems_ExpiredLease for rows
 *   whose lease has passed and returns them to 'Available'. Note the
 *   deliberate 30-second backoff on NextEligibleAtUtc so a worker that
 *   crashed mid-BLOB has time to be re-noticed by an orchestrator before
 *   the row is grabbed by yet another worker.
 *
 *   Runs on a SQL Agent job every 30 seconds.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_ReclaimExpiredLeases', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_ReclaimExpiredLeases;
GO
CREATE PROCEDURE dbo.usp_ReclaimExpiredLeases
    @BackoffSeconds INT = 30,
    @MaxRows        INT = 5000,
    @Reclaimed      INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();

    UPDATE TOP (@MaxRows) w
    SET Status              = N'Available',
        LastFailureReason   = N'Lease expired',
        LastFailureAtUtc    = @now,
        NextEligibleAtUtc   = DATEADD(SECOND, @BackoffSeconds, @now),
        ClaimedByWorkerId   = NULL,
        ClaimedByToken      = NULL,
        ClaimExpiresAtUtc   = NULL,
        ModifiedDate        = @now,
        ModifiedBy          = SUSER_SNAME()
    FROM dbo.ExportWorkItems AS w WITH (READPAST, ROWLOCK, UPDLOCK)
    WHERE w.Status            = N'Claimed'
      AND w.ClaimExpiresAtUtc < @now;

    SET @Reclaimed = @@ROWCOUNT;
    RETURN 0;
END
GO

/* Grants for the exporter role. */
GRANT EXECUTE ON dbo.usp_EnqueueWorkItems          TO [ExporterWriterRole];
GRANT EXECUTE ON dbo.usp_ClaimWorkItems            TO [ExporterWriterRole];
GRANT EXECUTE ON dbo.usp_RenewWorkItemLease        TO [ExporterWriterRole];
GRANT EXECUTE ON dbo.usp_CompleteWorkItem          TO [ExporterWriterRole];
GRANT EXECUTE ON dbo.usp_FailWorkItem              TO [ExporterWriterRole];
GRANT EXECUTE ON dbo.usp_ReclaimExpiredLeases      TO [ExporterWriterRole];
GRANT EXECUTE ON TYPE::dbo.udt_ExportWorkItemBatch TO [ExporterWriterRole];
GO

/****************************************************************************
 * SQL Agent registration for the reaper sweep.
 * Runs every 30 seconds.
 ****************************************************************************/
IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'msdb')
    AND SERVERPROPERTY('EngineEdition') <> 4
BEGIN
    DECLARE @reaperJob NVARCHAR(200) = N'MFilesExportTracking - ReclaimExpiredLeases';

    IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @reaperJob)
    BEGIN
        EXEC msdb.dbo.sp_add_job @job_name = @reaperJob, @enabled = 1,
            @description = N'Return expired-lease work items to Available.';

        EXEC msdb.dbo.sp_add_jobstep @job_name = @reaperJob, @step_name = N'Sweep',
            @subsystem = N'TSQL',
            @command = N'DECLARE @n INT; EXEC dbo.usp_ReclaimExpiredLeases @BackoffSeconds = 30, @MaxRows = 5000, @Reclaimed = @n OUTPUT;',
            @database_name = N'MFilesExportTracking';

        IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysschedules WHERE name = N'Every 30 seconds')
        BEGIN
            EXEC msdb.dbo.sp_add_schedule @schedule_name = N'Every 30 seconds',
                @freq_type = 4, @freq_interval = 1,
                @freq_subday_type = 2, @freq_subday_interval = 30;
        END

        EXEC msdb.dbo.sp_attach_schedule @job_name = @reaperJob, @schedule_name = N'Every 30 seconds';
        EXEC msdb.dbo.sp_add_jobserver @job_name = @reaperJob, @server_name = @@SERVERNAME;
    END
END
GO
