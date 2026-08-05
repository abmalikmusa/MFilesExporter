/****************************************************************************
 * File:        15-tvp-types.sql
 * Purpose:     Table-valued parameter (TVP) user types used by batch-insert
 *              stored procedures. TVPs are streamed to SQL Server from the
 *              client via IEnumerable<SqlDataRecord> — no DataTable required.
 ****************************************************************************/

USE [MFilesExportTracking];
GO

/* -------------------------------------------------------------------------
 * dbo.udt_ExportMetricBatch
 * ------------------------------------------------------------------------- */
IF TYPE_ID(N'dbo.udt_ExportMetricBatch') IS NULL
BEGIN
    CREATE TYPE dbo.udt_ExportMetricBatch AS TABLE
    (
        ExportJobId    BIGINT         NOT NULL,
        ExportWorkerId BIGINT         NULL,
        MetricName     NVARCHAR(200)  NOT NULL,
        MetricValue    FLOAT          NOT NULL,
        MetricUnit     NVARCHAR(50)   NOT NULL,
        Tags           NVARCHAR(2000) NULL,
        CapturedAtUtc  DATETIME2(3)   NOT NULL
    );
END
GO

/* -------------------------------------------------------------------------
 * dbo.udt_ExportProgressBatch
 * ------------------------------------------------------------------------- */
IF TYPE_ID(N'dbo.udt_ExportProgressBatch') IS NULL
BEGIN
    CREATE TYPE dbo.udt_ExportProgressBatch AS TABLE
    (
        ExportJobId            BIGINT        NOT NULL,
        ExportWorkerId         BIGINT        NULL,
        SnapshotAtUtc          DATETIME2(3)  NOT NULL,
        TotalRecorded          BIGINT        NOT NULL,
        TotalSucceeded         BIGINT        NOT NULL,
        TotalFailed            BIGINT        NOT NULL,
        TotalSkipped           BIGINT        NOT NULL,
        TotalBytesWritten      BIGINT        NOT NULL,
        DocumentsPerSecond     DECIMAL(18,4) NULL,
        MebibytesPerSecond     DECIMAL(18,4) NULL,
        LastDocumentFilePartId BIGINT        NULL,
        LastVersionPartId      BIGINT        NULL
    );
END
GO

/* -------------------------------------------------------------------------
 * dbo.udt_ExportErrorBatch
 * ------------------------------------------------------------------------- */
IF TYPE_ID(N'dbo.udt_ExportErrorBatch') IS NULL
BEGIN
    CREATE TYPE dbo.udt_ExportErrorBatch AS TABLE
    (
        ExportJobId        BIGINT         NOT NULL,
        ExportWorkerId     BIGINT         NULL,
        DocumentFilePartId BIGINT         NULL,
        VersionPartId      BIGINT         NULL,
        DataFileVersionId  BIGINT         NULL,
        IdempotencyKey     CHAR(64)       NULL,
        ErrorSeverity      NVARCHAR(16)   NOT NULL,
        ErrorCategory      NVARCHAR(32)   NOT NULL,
        ErrorSource        NVARCHAR(200)  NOT NULL,
        ExceptionType      NVARCHAR(400)  NULL,
        ErrorMessage       NVARCHAR(4000) NOT NULL,
        StackTrace         NVARCHAR(MAX)  NULL,
        AttemptNumber      INT            NOT NULL,
        OccurredAtUtc      DATETIME2(3)   NOT NULL
    );
END
GO

/* Grant so the exporter role can pass these TVPs. */
GRANT EXECUTE ON TYPE::dbo.udt_ExportMetricBatch   TO [ExporterWriterRole];
GRANT EXECUTE ON TYPE::dbo.udt_ExportProgressBatch TO [ExporterWriterRole];
GRANT EXECUTE ON TYPE::dbo.udt_ExportErrorBatch    TO [ExporterWriterRole];
GO
