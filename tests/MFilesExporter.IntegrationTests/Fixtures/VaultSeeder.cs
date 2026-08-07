using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace MFilesExporter.IntegrationTests.Fixtures;

/// <summary>
/// Populates the synthetic vault with N document versions. Each document has:
///   - a deterministic idempotency triple (part, version, dataFileVersion),
///   - a plausible TITLE + EXTENSION,
///   - a payload of varying size (128 B – 512 KiB) filled with a repeatable
///     byte pattern so tests can recompute the expected SHA-256 without
///     re-reading the vault.
/// </summary>
public static class VaultSeeder
{
    public sealed record SeededDocument(
        long   DocumentFilePart,
        int    VersionPart,
        long   DataFileVersion,
        string Title,
        string Extension,
        byte[] Payload,
        string ExpectedSha256Hex);

    public static async Task<IReadOnlyList<SeededDocument>> SeedAsync(
        string vaultConnectionString,
        int documentCount,
        int seed = 42)
    {
        var docs = new List<SeededDocument>(documentCount);
        var rng  = new Random(seed);

        for (var i = 0; i < documentCount; i++)
        {
            var part    = 1_000_000L + i;
            var version = 1;
            var dfv     = 5_000_000L + i;

            // Sizes distributed across three buckets: small (60 %), medium
            // (30 %), large (10 %). Keeps the run realistic without ballooning
            // the container's tempdb.
            var size = rng.Next(100) switch
            {
                < 60 => rng.Next(256, 4_096),
                < 90 => rng.Next(4_096, 65_536),
                _    => rng.Next(65_536, 524_288),
            };

            var payload = new byte[size];
            new Random(seed + i).NextBytes(payload);

            var ext = (i % 3) switch
            {
                0 => "pdf",
                1 => "docx",
                _ => "txt",
            };

            var title = $"doc_{part:D8}_v{version}";

            var checksum = SHA256.HashData(payload);
            var hex      = Convert.ToHexString(checksum).ToLowerInvariant();

            docs.Add(new SeededDocument(part, version, dfv, title, ext, payload, hex));
        }

        await using var conn = new SqlConnection(vaultConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        // Bulk insert via three prepared batches per document.
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);

        foreach (var d in docs)
        {
            await using (var cmd = new SqlCommand("""
                INSERT dbo.DATAFILEVERSION
                    (ID_DOCUMENTFILEPART, ID_DATAFILEVERSION, LOGICALFILESIZE, PHYSICALFILESIZE, LASTWRITETIME, UPLOADCOMMITTED)
                VALUES (@part, @dfv, @size, @size, SYSUTCDATETIME(), 1);

                INSERT dbo.DATAFILEVERSION_BYTES
                    (ID_DOCUMENTFILEPART, ID_DATAFILEVERSION, DATA)
                VALUES (@part, @dfv, @data);

                INSERT dbo.DOCUMENTFILEVERSION
                    (ID_DOCUMENTFILEPART, ID_VERSIONPART, DATAFILEVERSION, TITLE, EXTENSION)
                VALUES (@part, @ver, @dfv, @title, @ext);
            """, conn, tx))
            {
                cmd.CommandTimeout = 30;
                cmd.Parameters.Add("@dfv",  System.Data.SqlDbType.BigInt).Value    = d.DataFileVersion;
                cmd.Parameters.Add("@size", System.Data.SqlDbType.BigInt).Value    = (long)d.Payload.Length;
                cmd.Parameters.Add("@data", System.Data.SqlDbType.VarBinary, -1).Value = d.Payload;
                cmd.Parameters.Add("@part", System.Data.SqlDbType.BigInt).Value    = d.DocumentFilePart;
                cmd.Parameters.Add("@ver",  System.Data.SqlDbType.Int).Value        = d.VersionPart;
                cmd.Parameters.Add("@title",System.Data.SqlDbType.NVarChar, 255).Value = d.Title;
                cmd.Parameters.Add("@ext",  System.Data.SqlDbType.NVarChar, 32).Value  = d.Extension;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        await tx.CommitAsync().ConfigureAwait(false);
        return docs;
    }
}
