/****************************************************************************
 * File:        70-work-claiming-tables.sql
 * Purpose:     Distributed work-claiming table + supporting indexes.
 *              Combined with the stored procedures in 71-* these implement
 *              a lease-based work queue on top of SQL Server that
 *              guarantees at-most-once completion.
 *
 * Table:       dbo.ExportWorkItems
 *   One row per document to export. Rows enter as 'Available', are
 *   atomically claimed by workers (with a lease + claim token), and
 *   transition to 'Completed' only via a token-verified update. Expired
 *   leases return the row to 'Available' automatically.
 ****************************************************************************/

USE [MFilesExportTracking];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.ExportWorkItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExportWorkItems
    (
        WorkItemId              BIGINT           IDENTITY(1,1) NOT NULL,
        ExportJobId             BIGINT           NOT NULL,

        /* --- document identity (immutable after enqueue) --- */
        IdempotencyKey          CHAR(64)         NOT NULL,          -- SHA-256 hex of (part, ver, dfv)
        DocumentFilePartId      BIGINT           NOT NULL,
        VersionPartId           BIGINT           NOT NULL,
        DataFileVersionId       BIGINT           NOT NULL,
        Priority                INT              NOT NULL
                                                 CONSTRAINT DF_ExportWorkItems_Priority DEFAULT (0),

        /* --- claim state --- */
        Status                  NVARCHAR(20)     NOT NULL
                                                 CONSTRAINT DF_ExportWorkItems_Status DEFAULT (N'Available'),
        ClaimedByWorkerId       BIGINT           NULL,               -- FK to ExportWorkers
        ClaimedByToken          UNIQUEIDENTIFIER NULL,               -- per-claim GUID; the "fencing token"
        ClaimedAtUtc            DATETIME2(3)     NULL,
        ClaimExpiresAtUtc       DATETIME2(3)     NULL,               -- lease deadline

        /* --- retry accounting --- */
        AttemptCount            INT              NOT NULL
                                                 CONSTRAINT DF_ExportWorkItems_AttemptCount DEFAULT (0),
        MaxAttempts             INT              NOT NULL
                                                 CONSTRAINT DF_ExportWorkItems_MaxAttempts DEFAULT (5),
        LastFailureReason       NVARCHAR(2000)   NULL,
        LastFailureAtUtc        DATETIME2(3)     NULL,
        NextEligibleAtUtc       DATETIME2(3)     NOT NULL
                                                 CONSTRAINT DF_ExportWorkItems_NextEligibleAtUtc DEFAULT (SYSUTCDATETIME()),

        /* --- terminal artifact metadata (populated on Complete) --- */
        CompletedAtUtc          DATETIME2(3)     NULL,
        OutputPath              NVARCHAR(1024)   NULL,
        Checksum                CHAR(64)         NULL,               -- SHA-256 of written payload
        BytesWritten            BIGINT           NULL,

        /* --- standard audit --- */
        CreatedDate             DATETIME2(3)     NOT NULL
                                                 CONSTRAINT DF_ExportWorkItems_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy               NVARCHAR(128)    NOT NULL
                                                 CONSTRAINT DF_ExportWorkItems_CreatedBy DEFAULT (SUSER_SNAME()),
        ModifiedDate            DATETIME2(3)     NOT NULL
                                                 CONSTRAINT DF_ExportWorkItems_ModifiedDate DEFAULT (SYSUTCDATETIME()),
        ModifiedBy              NVARCHAR(128)    NOT NULL
                                                 CONSTRAINT DF_ExportWorkItems_ModifiedBy DEFAULT (SUSER_SNAME()),
        RowVersion              ROWVERSION       NOT NULL,

        CONSTRAINT PK_ExportWorkItems PRIMARY KEY CLUSTERED (WorkItemId),

        CONSTRAINT FK_ExportWorkItems_Job
            FOREIGN KEY (ExportJobId) REFERENCES dbo.ExportJobs (ExportJobId)
            ON DELETE NO ACTION ON UPDATE NO ACTION,

        CONSTRAINT FK_ExportWorkItems_Worker
            FOREIGN KEY (ClaimedByWorkerId) REFERENCES dbo.ExportWorkers (ExportWorkerId)
            ON DELETE NO ACTION ON UPDATE NO ACTION,

        /* Terminal-state enum. Note: no path back to Available from Completed. */
        CONSTRAINT CK_ExportWorkItems_Status
            CHECK (Status IN (N'Available', N'Claimed', N'Completed', N'Failed', N'DeadLettered')),

        /* Attempts must be non-negative and bounded. */
        CONSTRAINT CK_ExportWorkItems_Attempts
            CHECK (AttemptCount >= 0 AND MaxAttempts >= 1 AND AttemptCount <= MaxAttempts),

        /* Idempotency-key hex is 64 lowercase hex chars. */
        CONSTRAINT CK_ExportWorkItems_IdempotencyKey_Hex
            CHECK (LEN(IdempotencyKey) = 64
               AND IdempotencyKey NOT LIKE '%[^0-9a-f]%' COLLATE Latin1_General_BIN2),

        /* Uniqueness: exactly one row per (job, document). Enforces the
           "cannot enqueue the same document twice for a job" invariant. */
        CONSTRAINT UQ_ExportWorkItems_Job_Idempotency
            UNIQUE (ExportJobId, IdempotencyKey),

        /* If Status is Claimed, the token, worker, and lease must be set. */
        CONSTRAINT CK_ExportWorkItems_Claimed_Consistency
            CHECK (
                (Status <> N'Claimed')
             OR (Status = N'Claimed'
                 AND ClaimedByWorkerId IS NOT NULL
                 AND ClaimedByToken    IS NOT NULL
                 AND ClaimedAtUtc      IS NOT NULL
                 AND ClaimExpiresAtUtc IS NOT NULL)
            ),

        /* If Status is Completed, output columns must be populated. */
        CONSTRAINT CK_ExportWorkItems_Completed_Consistency
            CHECK (
                (Status <> N'Completed')
             OR (Status = N'Completed'
                 AND CompletedAtUtc  IS NOT NULL
                 AND OutputPath      IS NOT NULL
                 AND Checksum        IS NOT NULL
                 AND BytesWritten    IS NOT NULL)
            )
    );
END
GO

/****************************************************************************
 * Indexes.
 *   These carry the entire load of the claim engine and MUST be
 *   present before the procedures are called at scale.
 ****************************************************************************/

/* -----------------------------------------------------------------
 * IX_ExportWorkItems_ClaimCandidate
 *   Backs the atomic-claim UPDATE. Filtered to Status='Available' so
 *   the scan visits ONLY rows the claim query cares about. Sorted by
 *   Priority DESC, WorkItemId so the top-N pick is deterministic.
 * ----------------------------------------------------------------- */
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_ExportWorkItems_ClaimCandidate'
      AND object_id = OBJECT_ID(N'dbo.ExportWorkItems'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_ExportWorkItems_ClaimCandidate
        ON dbo.ExportWorkItems (ExportJobId, NextEligibleAtUtc, Priority DESC, WorkItemId)
        INCLUDE (Status, AttemptCount, MaxAttempts)
        WHERE Status = N'Available'
        WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 90, ONLINE = ON);
END
GO

/* -----------------------------------------------------------------
 * IX_ExportWorkItems_ExpiredLease
 *   Backs the lease-reaper sweep. Filtered to Status='Claimed' so the
 *   sweep only visits currently-claimed rows.
 * ----------------------------------------------------------------- */
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_ExportWorkItems_ExpiredLease'
      AND object_id = OBJECT_ID(N'dbo.ExportWorkItems'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_ExportWorkItems_ExpiredLease
        ON dbo.ExportWorkItems (ClaimExpiresAtUtc)
        INCLUDE (ExportJobId, ClaimedByWorkerId, ClaimedByToken, AttemptCount, MaxAttempts)
        WHERE Status = N'Claimed'
        WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 90, ONLINE = ON);
END
GO

/* -----------------------------------------------------------------
 * IX_ExportWorkItems_JobStatus
 *   Backs dashboard queries: "how many Available/Claimed/etc per job?".
 * ----------------------------------------------------------------- */
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_ExportWorkItems_JobStatus'
      AND object_id = OBJECT_ID(N'dbo.ExportWorkItems'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_ExportWorkItems_JobStatus
        ON dbo.ExportWorkItems (ExportJobId, Status)
        INCLUDE (AttemptCount, ClaimExpiresAtUtc)
        WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 90, ONLINE = ON);
END
GO

/* Grant table & procedure access to the exporter role (writer). */
GRANT SELECT, INSERT, UPDATE ON dbo.ExportWorkItems TO [ExporterWriterRole];
GO
