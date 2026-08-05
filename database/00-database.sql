/****************************************************************************
 * File:        00-database.sql
 * Database:    MFilesExportTracking (dedicated tracking store; NOT the vault)
 * Purpose:     Creates the tracking database used by MFilesExporter.
 *              The exporter is READ-ONLY against the M-Files vault. All state
 *              tracking, progress, worker registration, metrics, errors,
 *              checkpoints, and audit records live in THIS database.
 *
 * Idempotent:  Yes — safe to run against an existing instance; recreates
 *              only what does not already exist.
 * Author:      Seamfix Platform Engineering
 ****************************************************************************/

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

USE master;
GO

/* -------------------------------------------------------------------------
 * Create the database with production defaults.
 * ------------------------------------------------------------------------- */
IF DB_ID(N'MFilesExportTracking') IS NULL
BEGIN
    DECLARE @dataPath NVARCHAR(4000) =
        CONVERT(NVARCHAR(4000), SERVERPROPERTY('InstanceDefaultDataPath'));
    DECLARE @logPath  NVARCHAR(4000) =
        CONVERT(NVARCHAR(4000), SERVERPROPERTY('InstanceDefaultLogPath'));

    DECLARE @sql NVARCHAR(MAX) = N'
    CREATE DATABASE [MFilesExportTracking]
    ON PRIMARY
    (
        NAME = N''MFilesExportTracking_Data'',
        FILENAME = N''' + @dataPath + N'MFilesExportTracking_Data.mdf'',
        SIZE = 512 MB,
        MAXSIZE = UNLIMITED,
        FILEGROWTH = 256 MB
    )
    LOG ON
    (
        NAME = N''MFilesExportTracking_Log'',
        FILENAME = N''' + @logPath + N'MFilesExportTracking_Log.ldf'',
        SIZE = 128 MB,
        MAXSIZE = UNLIMITED,
        FILEGROWTH = 128 MB
    );';

    EXEC sys.sp_executesql @sql;
END
GO

/* -------------------------------------------------------------------------
 * Production settings — enforce full recovery, snapshot isolation for
 * read-mostly workloads, auto-update statistics async, and read-committed
 * snapshot to avoid reader/writer blocking.
 * ------------------------------------------------------------------------- */
ALTER DATABASE [MFilesExportTracking] SET RECOVERY FULL;
ALTER DATABASE [MFilesExportTracking] SET ALLOW_SNAPSHOT_ISOLATION ON;
ALTER DATABASE [MFilesExportTracking] SET READ_COMMITTED_SNAPSHOT ON WITH NO_WAIT;
ALTER DATABASE [MFilesExportTracking] SET AUTO_CREATE_STATISTICS ON;
ALTER DATABASE [MFilesExportTracking] SET AUTO_UPDATE_STATISTICS ON;
ALTER DATABASE [MFilesExportTracking] SET AUTO_UPDATE_STATISTICS_ASYNC ON;
ALTER DATABASE [MFilesExportTracking] SET PAGE_VERIFY CHECKSUM;
ALTER DATABASE [MFilesExportTracking] SET AUTO_SHRINK OFF;
ALTER DATABASE [MFilesExportTracking] SET AUTO_CLOSE OFF;
ALTER DATABASE [MFilesExportTracking] SET QUERY_STORE = ON
    (
        OPERATION_MODE = READ_WRITE,
        DATA_FLUSH_INTERVAL_SECONDS = 900,
        INTERVAL_LENGTH_MINUTES = 60,
        MAX_STORAGE_SIZE_MB = 2048,
        QUERY_CAPTURE_MODE = AUTO,
        SIZE_BASED_CLEANUP_MODE = AUTO
    );
GO

USE [MFilesExportTracking];
GO

/* -------------------------------------------------------------------------
 * Schemas — separate operational tables from archived rollups so retention
 * policies do not compete with hot indexes.
 * ------------------------------------------------------------------------- */
IF SCHEMA_ID(N'archive') IS NULL EXEC(N'CREATE SCHEMA [archive] AUTHORIZATION dbo;');
IF SCHEMA_ID(N'ops')     IS NULL EXEC(N'CREATE SCHEMA [ops]     AUTHORIZATION dbo;');
GO

/* -------------------------------------------------------------------------
 * Dedicated filegroups — separate history from hot data so
 * archive/read-mostly data can live on cheaper storage.
 * ------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE name = N'ArchiveFG')
BEGIN
    ALTER DATABASE [MFilesExportTracking] ADD FILEGROUP [ArchiveFG];

    DECLARE @archiveFile NVARCHAR(4000) =
        CONVERT(NVARCHAR(4000), SERVERPROPERTY('InstanceDefaultDataPath'))
        + N'MFilesExportTracking_Archive.ndf';

    DECLARE @sql NVARCHAR(MAX) = N'
    ALTER DATABASE [MFilesExportTracking] ADD FILE
    (
        NAME = N''MFilesExportTracking_Archive'',
        FILENAME = N''' + @archiveFile + N''',
        SIZE = 256 MB,
        MAXSIZE = UNLIMITED,
        FILEGROWTH = 256 MB
    ) TO FILEGROUP [ArchiveFG];';

    EXEC sys.sp_executesql @sql;
END
GO
