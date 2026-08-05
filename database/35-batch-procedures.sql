/****************************************************************************
 * File:        35-batch-procedures.sql
 * Purpose:     Batch stored procedures using the TVP types from 15-*.
 *              These procs are the hot path for the exporter's metric
 *              and progress ingest — one round-trip per batch, no per-row
 *              network overhead.
 ****************************************************************************/

USE [MFilesExportTracking];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* =========================================================================
 * usp_RecordExportMetricsBatch
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_RecordExportMetricsBatch', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_RecordExportMetricsBatch;
GO
CREATE PROCEDURE dbo.usp_RecordExportMetricsBatch
    @Metrics    dbo.udt_ExportMetricBatch READONLY,
    @ActorName  NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SET @ActorName = ISNULL(@ActorName, SUSER_SNAME());

    INSERT INTO dbo.ExportMetrics
        (ExportJobId, ExportWorkerId, MetricName, MetricValue, MetricUnit, Tags,
         CapturedAtUtc, Status, CreatedBy, ModifiedBy)
    SELECT
        ExportJobId, ExportWorkerId, MetricName, MetricValue, MetricUnit, Tags,
        CapturedAtUtc, N'Live', @ActorName, @ActorName
    FROM @Metrics;

    RETURN 0;
END
GO

/* =========================================================================
 * usp_RecordExportProgressBatch
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_RecordExportProgressBatch', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_RecordExportProgressBatch;
GO
CREATE PROCEDURE dbo.usp_RecordExportProgressBatch
    @Progress    dbo.udt_ExportProgressBatch READONLY,
    @ActorName   NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SET @ActorName = ISNULL(@ActorName, SUSER_SNAME());

    INSERT INTO dbo.ExportProgress
        (ExportJobId, ExportWorkerId, SnapshotAtUtc,
         TotalRecorded, TotalSucceeded, TotalFailed, TotalSkipped, TotalBytesWritten,
         DocumentsPerSecond, MebibytesPerSecond,
         LastDocumentFilePartId, LastVersionPartId,
         Status, CreatedBy, ModifiedBy)
    SELECT
        ExportJobId, ExportWorkerId, SnapshotAtUtc,
        TotalRecorded, TotalSucceeded, TotalFailed, TotalSkipped, TotalBytesWritten,
        DocumentsPerSecond, MebibytesPerSecond,
        LastDocumentFilePartId, LastVersionPartId,
        N'Snapshot', @ActorName, @ActorName
    FROM @Progress;

    RETURN 0;
END
GO

/* =========================================================================
 * usp_LogExportErrorsBatch — audit rows written per source row.
 * ========================================================================= */
IF OBJECT_ID(N'dbo.usp_LogExportErrorsBatch', N'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_LogExportErrorsBatch;
GO
CREATE PROCEDURE dbo.usp_LogExportErrorsBatch
    @Errors     dbo.udt_ExportErrorBatch READONLY,
    @ActorName  NVARCHAR(200) = NULL,
    @ActorType  NVARCHAR(32)  = N'Worker'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @ActorName = ISNULL(@ActorName, SUSER_SNAME());

    DECLARE @inserted TABLE
    (
        ExportErrorId BIGINT NOT NULL,
        ExportJobId   BIGINT NOT NULL,
        ErrorSeverity NVARCHAR(16) NOT NULL,
        ErrorCategory NVARCHAR(32) NOT NULL,
        ErrorSource   NVARCHAR(200) NOT NULL,
        ExceptionType NVARCHAR(400) NULL
    );

    BEGIN TRAN;

    INSERT INTO dbo.ExportErrors
        (ExportJobId, ExportWorkerId,
         DocumentFilePartId, VersionPartId, DataFileVersionId, IdempotencyKey,
         ErrorSeverity, ErrorCategory, ErrorSource,
         ExceptionType, ErrorMessage, StackTrace,
         AttemptNumber, OccurredAtUtc,
         Status, CreatedBy, ModifiedBy)
    OUTPUT inserted.ExportErrorId, inserted.ExportJobId,
           inserted.ErrorSeverity, inserted.ErrorCategory,
           inserted.ErrorSource, inserted.ExceptionType
        INTO @inserted (ExportErrorId, ExportJobId, ErrorSeverity, ErrorCategory, ErrorSource, ExceptionType)
    SELECT
        ExportJobId, ExportWorkerId,
        DocumentFilePartId, VersionPartId, DataFileVersionId, IdempotencyKey,
        ErrorSeverity, ErrorCategory, ErrorSource,
        ExceptionType, ErrorMessage, StackTrace,
        AttemptNumber, OccurredAtUtc,
        N'New', @ActorName, @ActorName
    FROM @Errors;

    INSERT INTO dbo.ExportAudit
        (ExportJobId, EntityType, EntityId, AuditAction,
         PreviousStatus, NewStatus, ActionDetails, ActorName, ActorType)
    SELECT
        ExportJobId, N'ExportErrors', ExportErrorId, N'ErrorRaised',
        NULL, N'New',
        (SELECT ErrorSeverity AS Severity,
                ErrorCategory AS Category,
                ErrorSource   AS Source,
                ExceptionType AS ExceptionType
         FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
        @ActorName, @ActorType
    FROM @inserted;

    COMMIT;
    RETURN 0;
END
GO

GRANT EXECUTE ON dbo.usp_RecordExportMetricsBatch   TO [ExporterWriterRole];
GRANT EXECUTE ON dbo.usp_RecordExportProgressBatch  TO [ExporterWriterRole];
GRANT EXECUTE ON dbo.usp_LogExportErrorsBatch       TO [ExporterWriterRole];
GO
