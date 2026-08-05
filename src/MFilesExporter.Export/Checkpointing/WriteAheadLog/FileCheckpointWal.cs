using System.Globalization;
using System.Text;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Checkpointing.WriteAheadLog;

/// <summary>
/// File-backed <see cref="ICheckpointWal"/>. Uses a *single-slot* atomic-
/// swap protocol for both durability and simplicity:
/// <list type="number">
///   <item><description>Serialize the entry to a UTF-8 line with a CRC-32 suffix.</description></item>
///   <item><description>Write the line to a temp sibling file.</description></item>
///   <item><description><c>Flush(flushToDisk: true)</c> — the fsync equivalent.</description></item>
///   <item><description><c>File.Move(temp, final, overwrite: true)</c> — atomic rename on NTFS and POSIX.</description></item>
/// </list>
/// The final file therefore either contains the last-known-good entry or
/// does not exist. A torn write on the temp file is invisible to readers.
/// </summary>
/// <remarks>
/// The single-slot approach is sufficient because the checkpoint is
/// monotonically increasing — losing the current record and reverting to
/// the previous one is unnecessary. On a power outage between temp-write
/// and rename, recovery returns the PREVIOUS slot value (still monotonic).
/// The idempotency layer in the work-claim engine guarantees no duplicate
/// exports even when the checkpoint reverts.
/// </remarks>
public sealed class FileCheckpointWal : ICheckpointWal
{
    private const string Separator = "|";

    private readonly CheckpointOptions _options;
    private readonly ILogger<FileCheckpointWal> _logger;

    public FileCheckpointWal(CheckpointOptions options, ILogger<FileCheckpointWal> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task AppendAsync(
        long jobId,
        string partitionKey,
        WalEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);

        Directory.CreateDirectory(_options.WalDirectory);

        var finalPath = ResolvePath(jobId, partitionKey);
        var tempPath = finalPath + ".tmp";
        var line = SerializeLine(entry);

        // Write + fsync temp file.
        await using (var stream = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4_096,
            FileOptions.Asynchronous))
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (_options.FsyncOnWrite)
            {
                // The critical durability call. Blocks until the OS confirms
                // the bytes have hit the physical medium.
                stream.Flush(flushToDisk: true);
            }
        }

        // Atomic swap — from this instant onward, readers see the new entry
        // or the previous one, never a truncated file.
        File.Move(tempPath, finalPath, overwrite: true);
    }

    public async Task<WalEntry?> ReadLatestAsync(
        long jobId,
        string partitionKey,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(jobId, partitionKey);
        if (!File.Exists(path))
        {
            return null;
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Checkpoint WAL read failed at {Path}; treating as no-checkpoint.", path);
            return null;
        }

        // Take the last non-empty line — the atomic-swap protocol guarantees
        // there's exactly one, but read defensively.
        var lastLine = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        if (string.IsNullOrEmpty(lastLine))
        {
            return null;
        }

        return TryDeserializeLine(lastLine, out var entry) ? entry : null;
    }

    private string ResolvePath(long jobId, string partitionKey)
    {
        // Encode partition into the filename defensively — partition strings
        // may include characters that are illegal in a POSIX/NTFS filename.
        var safePartition = SanitizePartition(partitionKey);
        return Path.Combine(_options.WalDirectory, $"checkpoint-{jobId}-{safePartition}.wal");
    }

    private static string SanitizePartition(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        var len = 0;
        foreach (var c in s)
        {
            buf[len++] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
        }
        return len == 0 ? "default" : new string(buf[..len]);
    }

    /* -----------------------------------------------------------------
     * Serialization format:
     *   part | ver | docsProcessed | isoUtc | crc32(part|ver|docs|iso)
     *
     * The CRC covers everything left of the final separator. On read,
     * we recompute over the same span and reject on mismatch.
     * ----------------------------------------------------------------- */

    private static string SerializeLine(WalEntry entry)
    {
        var payload = string.Join(Separator,
            entry.Cursor.DocumentFilePartId.ToString(CultureInfo.InvariantCulture),
            entry.Cursor.VersionPartId.ToString(CultureInfo.InvariantCulture),
            entry.DocumentsProcessedInPartition.ToString(CultureInfo.InvariantCulture),
            entry.PersistedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        var crc = Crc32.ComputeHex(payload);
        return payload + Separator + crc;
    }

    internal static bool TryDeserializeLine(string line, out WalEntry? entry)
    {
        entry = null;
        var parts = line.Split(Separator);
        if (parts.Length != 5) return false;

        var payload = string.Join(Separator, parts[..4]);
        var expectedCrc = Crc32.ComputeHex(payload);
        if (!string.Equals(expectedCrc, parts[4], StringComparison.OrdinalIgnoreCase))
        {
            // Torn write or manual edit — treat as no-checkpoint.
            return false;
        }

        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var docPart) ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var verPart) ||
            !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var docs) ||
            !DateTime.TryParse(parts[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
        {
            return false;
        }

        entry = new WalEntry(
            new DocumentFileVersionKey(docPart, verPart),
            docs,
            new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc)));
        return true;
    }
}
