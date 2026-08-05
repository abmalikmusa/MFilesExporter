/* =========================================================================
 * 05 · Recent errors (top 100)
 *
 * Latest error records with full context so ops can triage. Filter by
 * @JobId or @Since if the volume is high.
 * ========================================================================= */
DECLARE @JobId BIGINT       = NULL;
DECLARE @Since DATETIME2(3) = NULL;

WITH TargetJob AS
(
    SELECT TOP (1) ExportJobId
    FROM   dbo.ExportJobs
    WHERE  (@JobId IS NULL AND Status = N'Running')
        OR (ExportJobId = @JobId)
    ORDER  BY StartedAtUtc DESC, ExportJobId DESC
)
SELECT TOP (100)
       e.OccurredAtUtc,
       e.ErrorCategory,
       e.ErrorSeverity,
       e.Status,
       e.DocumentFilePart,
       e.VersionPart,
       e.DataFileVersion,
       e.WorkerName,
       ErrorMessage = LEFT(e.ErrorMessage, 500),
       StackHint    = LEFT(ISNULL(e.StackTrace, N''), 200)
FROM   dbo.ExportErrors AS e
JOIN   TargetJob        AS t ON t.ExportJobId = e.ExportJobId
WHERE  (@Since IS NULL OR e.OccurredAtUtc >= @Since)
ORDER  BY e.OccurredAtUtc DESC, e.ExportErrorId DESC;
