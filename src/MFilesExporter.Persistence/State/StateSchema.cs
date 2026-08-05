namespace MFilesExporter.Persistence.State;

internal static class StateSchema
{
    public const int SchemaVersion = 1;

    public static readonly string[] Statements =
    {
        @"CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER NOT NULL PRIMARY KEY,
            applied_at_utc TEXT NOT NULL);",
        @"INSERT OR IGNORE INTO schema_version (version, applied_at_utc)
          VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));",
        @"CREATE TABLE IF NOT EXISTS checkpoints (
            partition_key TEXT NOT NULL PRIMARY KEY,
            last_document_file_part_id INTEGER NOT NULL,
            last_version_part_id INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL);",
        @"CREATE TABLE IF NOT EXISTS export_outcomes (
            idempotency_key BLOB NOT NULL PRIMARY KEY,
            document_file_part_id INTEGER NOT NULL,
            version_part_id INTEGER NOT NULL,
            data_file_version_id INTEGER NOT NULL,
            status INTEGER NOT NULL,
            bytes_written INTEGER NOT NULL,
            output_path TEXT,
            checksum TEXT,
            failure_reason TEXT,
            observed_at_utc TEXT NOT NULL,
            attempt_number INTEGER NOT NULL
        ) WITHOUT ROWID;",
        @"CREATE INDEX IF NOT EXISTS idx_outcomes_status ON export_outcomes(status);",
        @"CREATE INDEX IF NOT EXISTS idx_outcomes_docpart_version
            ON export_outcomes(document_file_part_id, version_part_id);",
        @"CREATE TABLE IF NOT EXISTS export_counters (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 0),
            total_recorded INTEGER NOT NULL DEFAULT 0,
            total_succeeded INTEGER NOT NULL DEFAULT 0,
            total_failed INTEGER NOT NULL DEFAULT 0,
            total_skipped INTEGER NOT NULL DEFAULT 0,
            total_bytes_written INTEGER NOT NULL DEFAULT 0);",
        @"INSERT OR IGNORE INTO export_counters (singleton) VALUES (0);",
        @"CREATE TRIGGER IF NOT EXISTS trg_outcomes_insert
          AFTER INSERT ON export_outcomes
          BEGIN
              UPDATE export_counters SET
                total_recorded = total_recorded + 1,
                total_succeeded = total_succeeded + CASE NEW.status WHEN 2 THEN 1 ELSE 0 END,
                total_failed    = total_failed    + CASE NEW.status WHEN 3 THEN 1 ELSE 0 END,
                total_skipped   = total_skipped   + CASE NEW.status WHEN 4 THEN 1 ELSE 0 END,
                total_bytes_written = total_bytes_written + NEW.bytes_written
              WHERE singleton = 0;
          END;",
        @"CREATE TRIGGER IF NOT EXISTS trg_outcomes_update
          AFTER UPDATE ON export_outcomes
          BEGIN
              UPDATE export_counters SET
                total_succeeded = total_succeeded
                    - CASE OLD.status WHEN 2 THEN 1 ELSE 0 END
                    + CASE NEW.status WHEN 2 THEN 1 ELSE 0 END,
                total_failed = total_failed
                    - CASE OLD.status WHEN 3 THEN 1 ELSE 0 END
                    + CASE NEW.status WHEN 3 THEN 1 ELSE 0 END,
                total_skipped = total_skipped
                    - CASE OLD.status WHEN 4 THEN 1 ELSE 0 END
                    + CASE NEW.status WHEN 4 THEN 1 ELSE 0 END,
                total_bytes_written = total_bytes_written - OLD.bytes_written + NEW.bytes_written
              WHERE singleton = 0;
          END;",
    };
}
