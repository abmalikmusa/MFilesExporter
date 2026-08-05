/****************************************************************************
 * File:        50-security.sql
 * Purpose:     Least-privilege principals for the exporter and operators.
 *
 * Principals created:
 *   [mfilesexporter_writer]   — the exporter's login. Executes usp_* only.
 *   [mfilesexporter_reader]   — read-only login for BI / dashboards.
 *
 * Neither login has ad-hoc DML rights on the tables. The exporter must go
 * through the stored procedures; BI must go through the views.
 ****************************************************************************/

USE [master];
GO

/* -------------------------------------------------------------------------
 * Server-level logins (SQL Auth here; swap to WINDOWS AUTH in production).
 * ------------------------------------------------------------------------- */
IF SUSER_ID(N'mfilesexporter_writer') IS NULL
    CREATE LOGIN [mfilesexporter_writer] WITH PASSWORD = N'REPLACE_ME_STRONG_PASSWORD_1',
        CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;
IF SUSER_ID(N'mfilesexporter_reader') IS NULL
    CREATE LOGIN [mfilesexporter_reader] WITH PASSWORD = N'REPLACE_ME_STRONG_PASSWORD_2',
        CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;
GO

USE [MFilesExportTracking];
GO

/* -------------------------------------------------------------------------
 * Database users.
 * ------------------------------------------------------------------------- */
IF USER_ID(N'mfilesexporter_writer') IS NULL
    CREATE USER [mfilesexporter_writer] FOR LOGIN [mfilesexporter_writer];
IF USER_ID(N'mfilesexporter_reader') IS NULL
    CREATE USER [mfilesexporter_reader] FOR LOGIN [mfilesexporter_reader];
GO

/* -------------------------------------------------------------------------
 * Roles.
 * ------------------------------------------------------------------------- */
IF DATABASE_PRINCIPAL_ID(N'ExporterWriterRole') IS NULL
    CREATE ROLE [ExporterWriterRole] AUTHORIZATION [dbo];
IF DATABASE_PRINCIPAL_ID(N'ExporterReaderRole') IS NULL
    CREATE ROLE [ExporterReaderRole] AUTHORIZATION [dbo];
GO

/* -------------------------------------------------------------------------
 * Writer role: EXECUTE on all usp_* procedures in dbo, plus SELECT on the
 * views. NO direct DML on tables — the exporter cannot bypass the API.
 * ------------------------------------------------------------------------- */
GRANT EXECUTE ON SCHEMA::dbo TO [ExporterWriterRole];
GRANT SELECT  ON SCHEMA::dbo TO [ExporterWriterRole];   -- table + view read
DENY   INSERT, UPDATE, DELETE ON SCHEMA::dbo TO [ExporterWriterRole];
GO

/* -------------------------------------------------------------------------
 * Reader role: SELECT on the vw_* views. Nothing else.
 * ------------------------------------------------------------------------- */
GRANT SELECT ON dbo.vw_ActiveJobs          TO [ExporterReaderRole];
GRANT SELECT ON dbo.vw_JobSummary          TO [ExporterReaderRole];
GRANT SELECT ON dbo.vw_WorkerHealth        TO [ExporterReaderRole];
GRANT SELECT ON dbo.vw_ErrorSummary        TO [ExporterReaderRole];
GRANT SELECT ON dbo.vw_ThroughputHourly    TO [ExporterReaderRole];
GRANT SELECT ON dbo.vw_CheckpointCurrent   TO [ExporterReaderRole];
GRANT SELECT ON dbo.vw_AuditRecent         TO [ExporterReaderRole];
GO

/* -------------------------------------------------------------------------
 * Add users to roles.
 * ------------------------------------------------------------------------- */
ALTER ROLE [ExporterWriterRole] ADD MEMBER [mfilesexporter_writer];
ALTER ROLE [ExporterReaderRole] ADD MEMBER [mfilesexporter_reader];
GO
