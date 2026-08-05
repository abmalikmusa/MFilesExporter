/* =========================================================================
 * 08 · Current checkpoint
 *
 * Latest saved checkpoint per partition. Used to confirm a graceful
 * restart resumes from the right position — the WAL should never be
 * ahead of the number shown here after `Stop-Service` completes.
 * ========================================================================= */
SELECT
    PartitionKey,
    DocumentFilePart,
    VersionPart,
    DataFileVersion,
    SavedAtUtc,
    SavedByWorker
FROM   dbo.vw_CheckpointCurrent
ORDER  BY PartitionKey;
