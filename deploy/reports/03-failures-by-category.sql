/* =========================================================================
 * 03 · Failures by category
 *
 * Groups the ExportErrors table by category + severity so ops can see
 * which failure modes dominate. Top 20 rows sorted by count desc.
 *
 * Parameters
 *   @JobId — leave NULL to pick the most recent Running job.
 *   @Since — leave NULL to look at the full history. Set to a UTC
 *            datetime to scope to the last N minutes/hours.
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
SELECT TOP (20)
       ErrorCategory,
       ErrorSeverity,
       Occurrences = COUNT(*),
       FirstSeenUtc = MIN(OccurredAtUtc),
       LastSeenUtc  = MAX(OccurredAtUtc),
       SampleMessage = MAX(ErrorMessage)
FROM   dbo.ExportErrors AS e
JOIN   TargetJob        AS t ON t.ExportJobId = e.ExportJobId
WHERE  (@Since IS NULL OR e.OccurredAtUtc >= @Since)
GROUP  BY ErrorCategory, ErrorSeverity
ORDER  BY COUNT(*) DESC;
