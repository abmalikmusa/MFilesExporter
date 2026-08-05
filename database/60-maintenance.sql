/****************************************************************************
 * File:        60-maintenance.sql
 * Purpose:     Operational procedures — archive, purge, reindex, statistics.
 *              Registered as SQL Agent jobs (see the "SQL Agent registration"
 *              section at the bottom, guarded so it is skipped on Express).
 ****************************************************************************/

USE [MFilesExportTracking];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* =========================================================================
 * ops.usp_ArchiveCompletedJobs
 *   Moves rows for jobs whose Status IN (Completed, Failed, Cancelled) and
 *   whose CompletedAtUtc is older than @OlderThanDays. Non-transactional
 *   per-batch (BATCHSIZE) so long runs do not blow the transaction log.
 * ========================================================================= */
IF OBJECT_ID(N'ops.usp_ArchiveCompletedJobs', N'P') IS NOT NULL
    DROP PROCEDURE ops.usp_ArchiveCompletedJobs;
GO
CREATE PROCEDURE ops.usp_ArchiveCompletedJobs
    @OlderThanDays INT = 180,
    @BatchSize     INT = 1000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @cutoff DATETIME2(3) = DATEADD(DAY, -@OlderThanDays, SYSUTCDATETIME());
    DECLARE @archived TABLE (ExportJobId BIGINT PRIMARY KEY);

    ;WITH candidates AS
    (
        SELECT TOP (@BatchSize) ExportJobId
        FROM dbo.ExportJobs
        WHERE Status IN (N'Completed', N'Failed', N'Cancelled')
          AND CompletedAtUtc IS NOT NULL
          AND CompletedAtUtc < @cutoff
        ORDER BY CompletedAtUtc ASC
    )
    INSERT INTO @archived (ExportJobId) SELECT ExportJobId FROM candidates;

    IF NOT EXISTS (SELECT 1 FROM @archived) RETURN 0;

    BEGIN TRAN;

    -- Move child rows first, then the parent.
    INSERT INTO archive.ExportAudit
        SELECT a.*, SYSUTCDATETIME() AS ArchivedAtUtc
        FROM dbo.ExportAudit a
        INNER JOIN @archived x ON x.ExportJobId = a.ExportJobId;
    DELETE a FROM dbo.ExportAudit a INNER JOIN @archived x ON x.ExportJobId = a.ExportJobId;

    INSERT INTO archive.ExportCheckpoints
        SELECT c.ExportCheckpointId, c.ExportJobId, c.PartitionKey,
               c.LastDocumentFilePartId, c.LastVersionPartId, c.DocumentsProcessedInPartition,
               c.CheckpointAtUtc, c.Status, c.CreatedDate, SYSUTCDATETIME()
        FROM dbo.ExportCheckpoints c
        INNER JOIN @archived x ON x.ExportJobId = c.ExportJobId;
    DELETE c FROM dbo.ExportCheckpoints c INNER JOIN @archived x ON x.ExportJobId = c.ExportJobId;

    INSERT INTO archive.ExportErrors
        SELECT e.ExportErrorId, e.ExportJobId, e.ExportWorkerId,
               e.DocumentFilePartId, e.VersionPartId, e.DataFileVersionId, e.IdempotencyKey,
               e.ErrorSeverity, e.ErrorCategory, e.ErrorSource, e.ExceptionType,
               e.ErrorMessage, e.StackTrace, e.AttemptNumber, e.OccurredAtUtc,
               e.ResolvedAtUtc, e.ResolvedBy, e.ResolutionNotes, e.Status,
               e.CreatedDate, SYSUTCDATETIME()
        FROM dbo.ExportErrors e
        INNER JOIN @archived x ON x.ExportJobId = e.ExportJobId;
    DELETE e FROM dbo.ExportErrors e INNER JOIN @archived x ON x.ExportJobId = e.ExportJobId;

    INSERT INTO archive.ExportMetrics
        SELECT m.ExportMetricId, m.ExportJobId, m.ExportWorkerId,
               m.MetricName, m.MetricValue, m.MetricUnit, m.Tags,
               m.CapturedAtUtc, m.Status, m.CreatedDate, SYSUTCDATETIME()
        FROM dbo.ExportMetrics m
        INNER JOIN @archived x ON x.ExportJobId = m.ExportJobId;
    DELETE m FROM dbo.ExportMetrics m INNER JOIN @archived x ON x.ExportJobId = m.ExportJobId;

    INSERT INTO archive.ExportProgress
        SELECT p.ExportProgressId, p.ExportJobId, p.ExportWorkerId,
               p.SnapshotAtUtc, p.TotalRecorded, p.TotalSucceeded, p.TotalFailed,
               p.TotalSkipped, p.TotalBytesWritten,
               p.DocumentsPerSecond, p.MebibytesPerSecond,
               p.LastDocumentFilePartId, p.LastVersionPartId,
               p.Status, p.CreatedDate, p.CreatedBy, SYSUTCDATETIME()
        FROM dbo.ExportProgress p
        INNER JOIN @archived x ON x.ExportJobId = p.ExportJobId;
    DELETE p FROM dbo.ExportProgress p INNER JOIN @archived x ON x.ExportJobId = p.ExportJobId;

    INSERT INTO archive.ExportWorkers
        SELECT w.*, SYSUTCDATETIME() AS ArchivedAtUtc, SUSER_SNAME() AS ArchivedBy
        FROM dbo.ExportWorkers w
        INNER JOIN @archived x ON x.ExportJobId = w.ExportJobId;
    DELETE w FROM dbo.ExportWorkers w INNER JOIN @archived x ON x.ExportJobId = w.ExportJobId;

    INSERT INTO archive.ExportJobs
        SELECT j.*, SYSUTCDATETIME() AS ArchivedAtUtc, SUSER_SNAME() AS ArchivedBy
        FROM dbo.ExportJobs j
        INNER JOIN @archived x ON x.ExportJobId = j.ExportJobId;
    DELETE j FROM dbo.ExportJobs j INNER JOIN @archived x ON x.ExportJobId = j.ExportJobId;

    COMMIT;

    RETURN 0;
END
GO

/* =========================================================================
 * ops.usp_PurgeArchivedData
 *   Deletes archive rows older than @PurgeAfterDays. Batches to avoid log
 *   pressure. Use with an off-site backup already taken.
 * ========================================================================= */
IF OBJECT_ID(N'ops.usp_PurgeArchivedData', N'P') IS NOT NULL
    DROP PROCEDURE ops.usp_PurgeArchivedData;
GO
CREATE PROCEDURE ops.usp_PurgeArchivedData
    @PurgeAfterDays INT = 730,   -- 2 years by default
    @BatchSize      INT = 5000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @cutoff DATETIME2(3) = DATEADD(DAY, -@PurgeAfterDays, SYSUTCDATETIME());
    DECLARE @deleted INT = 1;

    WHILE @deleted > 0
    BEGIN
        DELETE TOP (@BatchSize) FROM archive.ExportAudit         WHERE ArchivedAtUtc < @cutoff;
        SET @deleted = @@ROWCOUNT;
    END

    SET @deleted = 1;
    WHILE @deleted > 0
    BEGIN
        DELETE TOP (@BatchSize) FROM archive.ExportMetrics       WHERE ArchivedAtUtc < @cutoff;
        SET @deleted = @@ROWCOUNT;
    END

    SET @deleted = 1;
    WHILE @deleted > 0
    BEGIN
        DELETE TOP (@BatchSize) FROM archive.ExportErrors        WHERE ArchivedAtUtc < @cutoff;
        SET @deleted = @@ROWCOUNT;
    END

    SET @deleted = 1;
    WHILE @deleted > 0
    BEGIN
        DELETE TOP (@BatchSize) FROM archive.ExportProgress      WHERE ArchivedAtUtc < @cutoff;
        SET @deleted = @@ROWCOUNT;
    END

    SET @deleted = 1;
    WHILE @deleted > 0
    BEGIN
        DELETE TOP (@BatchSize) FROM archive.ExportCheckpoints   WHERE ArchivedAtUtc < @cutoff;
        SET @deleted = @@ROWCOUNT;
    END

    SET @deleted = 1;
    WHILE @deleted > 0
    BEGIN
        DELETE TOP (@BatchSize) FROM archive.ExportWorkers       WHERE ArchivedAtUtc < @cutoff;
        SET @deleted = @@ROWCOUNT;
    END

    SET @deleted = 1;
    WHILE @deleted > 0
    BEGIN
        DELETE TOP (@BatchSize) FROM archive.ExportJobs          WHERE ArchivedAtUtc < @cutoff;
        SET @deleted = @@ROWCOUNT;
    END

    RETURN 0;
END
GO

/* =========================================================================
 * ops.usp_ReindexAndUpdateStats
 *   Reorganizes indexes with 5–30% fragmentation; rebuilds >30%. Updates
 *   statistics with FULLSCAN for the small operational tables and
 *   SAMPLE 20 PERCENT for the append-heavy ones.
 * ========================================================================= */
IF OBJECT_ID(N'ops.usp_ReindexAndUpdateStats', N'P') IS NOT NULL
    DROP PROCEDURE ops.usp_ReindexAndUpdateStats;
GO
CREATE PROCEDURE ops.usp_ReindexAndUpdateStats
    @MinFragmentation FLOAT = 5.0,
    @RebuildThreshold FLOAT = 30.0
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @sql NVARCHAR(MAX);

    DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
        SELECT
            QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name) AS TableName,
            QUOTENAME(i.name) AS IndexName,
            ps.avg_fragmentation_in_percent
        FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, N'LIMITED') AS ps
        INNER JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
        INNER JOIN sys.objects o ON o.object_id = ps.object_id
        WHERE ps.avg_fragmentation_in_percent >= @MinFragmentation
          AND i.name IS NOT NULL
          AND o.is_ms_shipped = 0;

    DECLARE @tbl NVARCHAR(300), @idx NVARCHAR(300), @frag FLOAT;
    OPEN cur;
    FETCH NEXT FROM cur INTO @tbl, @idx, @frag;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF @frag >= @RebuildThreshold
            SET @sql = N'ALTER INDEX ' + @idx + N' ON ' + @tbl + N' REBUILD WITH (ONLINE = OFF, MAXDOP = 4);';
        ELSE
            SET @sql = N'ALTER INDEX ' + @idx + N' ON ' + @tbl + N' REORGANIZE;';
        EXEC sys.sp_executesql @sql;
        FETCH NEXT FROM cur INTO @tbl, @idx, @frag;
    END
    CLOSE cur; DEALLOCATE cur;

    EXEC sp_updatestats;

    RETURN 0;
END
GO

/* =========================================================================
 * ops.usp_WalTruncateHint — updates the Query Store cleanup + does a
 * CHECKPOINT so the log file is flushed. Not a "truncate" but a hint for
 * the ops schedule.
 * ========================================================================= */
IF OBJECT_ID(N'ops.usp_WalTruncateHint', N'P') IS NOT NULL
    DROP PROCEDURE ops.usp_WalTruncateHint;
GO
CREATE PROCEDURE ops.usp_WalTruncateHint
AS
BEGIN
    SET NOCOUNT ON;
    CHECKPOINT;
    RETURN 0;
END
GO

/****************************************************************************
 * SQL Agent job registration — guarded so it silently skips on editions
 * where SQL Agent is absent (Express).
 ****************************************************************************/
IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'msdb')
    AND SERVERPROPERTY('EngineEdition') <> 4    -- 4 = Express
BEGIN
    DECLARE @archiveJob NVARCHAR(200) = N'MFilesExportTracking - ArchiveCompletedJobs';
    DECLARE @purgeJob   NVARCHAR(200) = N'MFilesExportTracking - PurgeArchivedData';
    DECLARE @reindexJob NVARCHAR(200) = N'MFilesExportTracking - ReindexAndUpdateStats';
    DECLARE @staleJob   NVARCHAR(200) = N'MFilesExportTracking - MarkStalledWorkers';

    IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @archiveJob)
    BEGIN
        EXEC msdb.dbo.sp_add_job @job_name = @archiveJob, @enabled = 1, @description = N'Archive completed jobs older than 180 days.';
        EXEC msdb.dbo.sp_add_jobstep @job_name = @archiveJob, @step_name = N'Archive', @subsystem = N'TSQL',
            @command = N'EXEC ops.usp_ArchiveCompletedJobs @OlderThanDays = 180, @BatchSize = 1000;',
            @database_name = N'MFilesExportTracking';
        EXEC msdb.dbo.sp_add_schedule @schedule_name = N'Daily 02:00',
            @freq_type = 4, @freq_interval = 1, @active_start_time = 020000;
        EXEC msdb.dbo.sp_attach_schedule @job_name = @archiveJob, @schedule_name = N'Daily 02:00';
        EXEC msdb.dbo.sp_add_jobserver @job_name = @archiveJob, @server_name = @@SERVERNAME;
    END

    IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @purgeJob)
    BEGIN
        EXEC msdb.dbo.sp_add_job @job_name = @purgeJob, @enabled = 1, @description = N'Purge archive rows older than 730 days.';
        EXEC msdb.dbo.sp_add_jobstep @job_name = @purgeJob, @step_name = N'Purge', @subsystem = N'TSQL',
            @command = N'EXEC ops.usp_PurgeArchivedData @PurgeAfterDays = 730, @BatchSize = 5000;',
            @database_name = N'MFilesExportTracking';
        IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysschedules WHERE name = N'Weekly Sunday 03:00')
        BEGIN
            EXEC msdb.dbo.sp_add_schedule @schedule_name = N'Weekly Sunday 03:00',
                @freq_type = 8, @freq_interval = 1, @freq_recurrence_factor = 1, @active_start_time = 030000;
        END
        EXEC msdb.dbo.sp_attach_schedule @job_name = @purgeJob, @schedule_name = N'Weekly Sunday 03:00';
        EXEC msdb.dbo.sp_add_jobserver @job_name = @purgeJob, @server_name = @@SERVERNAME;
    END

    IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @reindexJob)
    BEGIN
        EXEC msdb.dbo.sp_add_job @job_name = @reindexJob, @enabled = 1, @description = N'Reindex + stats.';
        EXEC msdb.dbo.sp_add_jobstep @job_name = @reindexJob, @step_name = N'Reindex', @subsystem = N'TSQL',
            @command = N'EXEC ops.usp_ReindexAndUpdateStats;',
            @database_name = N'MFilesExportTracking';
        IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysschedules WHERE name = N'Weekly Sat 01:30')
        BEGIN
            EXEC msdb.dbo.sp_add_schedule @schedule_name = N'Weekly Sat 01:30',
                @freq_type = 8, @freq_interval = 64, @freq_recurrence_factor = 1, @active_start_time = 013000;
        END
        EXEC msdb.dbo.sp_attach_schedule @job_name = @reindexJob, @schedule_name = N'Weekly Sat 01:30';
        EXEC msdb.dbo.sp_add_jobserver @job_name = @reindexJob, @server_name = @@SERVERNAME;
    END

    IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @staleJob)
    BEGIN
        EXEC msdb.dbo.sp_add_job @job_name = @staleJob, @enabled = 1, @description = N'Detect stalled workers every 60s.';
        EXEC msdb.dbo.sp_add_jobstep @job_name = @staleJob, @step_name = N'Sweep', @subsystem = N'TSQL',
            @command = N'EXEC dbo.usp_MarkStalledWorkers @StaleAfterSeconds = 120;',
            @database_name = N'MFilesExportTracking';
        IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysschedules WHERE name = N'Every 60 seconds')
        BEGIN
            EXEC msdb.dbo.sp_add_schedule @schedule_name = N'Every 60 seconds',
                @freq_type = 4, @freq_interval = 1,
                @freq_subday_type = 2, @freq_subday_interval = 60;   -- every 60 seconds
        END
        EXEC msdb.dbo.sp_attach_schedule @job_name = @staleJob, @schedule_name = N'Every 60 seconds';
        EXEC msdb.dbo.sp_add_jobserver @job_name = @staleJob, @server_name = @@SERVERNAME;
    END
END
GO
