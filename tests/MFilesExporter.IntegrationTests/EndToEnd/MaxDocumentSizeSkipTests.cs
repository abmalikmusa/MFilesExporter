using FluentAssertions;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.IntegrationTests.Fixtures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace MFilesExporter.IntegrationTests.EndToEnd;

/// <summary>
/// End-to-end coverage of <see cref="PipelineOptions.MaxDocumentSizeMb"/>.
/// The option is a hot-path guard in <c>ProducerStage</c> that silently
/// drops descriptors bigger than the configured ceiling — designed to
/// prevent a single 20 GB document from dominating an export run. Until
/// now the branch was only unit-tested; this proves it actually fires
/// when the pipeline runs against a mixed-size vault.
/// </summary>
[Collection("SqlServer")]
public sealed class MaxDocumentSizeSkipTests
{
    private const int SmallCount     = 12;
    private const int OversizeCount  = 4;
    private const int MaxSizeMb      = 1;
    private const string Partition   = "size-skip";

    private readonly SqlServerFixture _sql;

    public MaxDocumentSizeSkipTests(SqlServerFixture sql) => _sql = sql;

    [Fact]
    public async Task Documents_ExceedingLimit_AreSkipped_UnderSizeOnesSucceed()
    {
        // Small docs go in via the shared seeder; oversize ones are inserted
        // inline so we control the LOGICALFILESIZE column precisely.
        await VaultSeeder.ResetAsync(_sql.SourceConnectionString).ConfigureAwait(false);
        await VaultSeeder.SeedAsync(
            _sql.SourceConnectionString,
            SmallCount,
            seed: 20260812,
            partStartId: 12_000_000L,
            dfvStartId:  16_000_000L).ConfigureAwait(false);

        await InsertOversizeDocumentsAsync(
            _sql.SourceConnectionString,
            OversizeCount,
            partStartId: 12_500_000L,
            dfvStartId:  16_500_000L,
            payloadSizeBytes: (MaxSizeMb + 1) * 1024L * 1024L).ConfigureAwait(false);

        await using var host = ExporterTestHost.Create(
            _sql,
            workerCount: 2,
            partitionKey: Partition,
            customize: services =>
            {
                // Turn the oversize guard on for this run only.
                services.PostConfigure<ExporterOptions>(o =>
                {
                    o.Pipeline.MaxDocumentSizeMb = MaxSizeMb;
                });
            });

        await host.Services.GetRequiredService<IExportStateStore>()
            .InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await host.Pipeline.RunAsync(cts.Token).ConfigureAwait(false);

        // -----------------------------------------------------------------
        // Assert — the small docs made it through; the oversize ones were
        // silently dropped by the ProducerStage size guard.
        // -----------------------------------------------------------------
        var counters = await host.Services.GetRequiredService<IExportStateStore>()
            .GetCountersAsync(CancellationToken.None).ConfigureAwait(false);

        counters.TotalRecorded.Should().Be(SmallCount,
            "the producer must skip oversize descriptors BEFORE they reach the state store");
        counters.TotalSucceeded.Should().Be(SmallCount);
        counters.TotalFailed.Should().Be(0);

        // Files landed only for the small docs — nothing over the threshold.
        var writtenFiles = Directory.EnumerateFiles(
            Path.Combine(host.OutputRoot, "documents"), "*", SearchOption.AllDirectories).ToArray();
        writtenFiles.Should().HaveCount(SmallCount);
        writtenFiles.Should().OnlyContain(
            path => new FileInfo(path).Length < (long)MaxSizeMb * 1024L * 1024L,
            "no landed file should be at or above the MaxDocumentSizeMb ceiling");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static async Task InsertOversizeDocumentsAsync(
        string vaultConnectionString,
        int count,
        long partStartId,
        long dfvStartId,
        long payloadSizeBytes)
    {
        // A one-byte payload with an inflated LOGICALFILESIZE would trigger
        // the guard just as well, but we honour the schema contract: the
        // payload bytes match the reported size. Keeps the vault self-
        // consistent and mirrors real M-Files data.
        var payload = new byte[payloadSizeBytes];
        Array.Fill(payload, (byte)'X');

        await using var conn = new SqlConnection(vaultConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);

        for (var i = 0; i < count; i++)
        {
            var part = partStartId + i;
            var dfv  = dfvStartId  + i;

            await using var cmd = new SqlCommand("""
                INSERT dbo.DATAFILEVERSION
                    (ID_DOCUMENTFILEPART, ID_DATAFILEVERSION, LOGICALFILESIZE, PHYSICALFILESIZE, LASTWRITETIME, UPLOADCOMMITTED)
                VALUES (@part, @dfv, @size, @size, SYSUTCDATETIME(), 1);

                INSERT dbo.DATAFILEVERSION_BYTES
                    (ID_DOCUMENTFILEPART, ID_DATAFILEVERSION, DATA)
                VALUES (@part, @dfv, @data);

                INSERT dbo.DOCUMENTFILEVERSION
                    (ID_DOCUMENTFILEPART, ID_VERSIONPART, DATAFILEVERSION, TITLE, EXTENSION)
                VALUES (@part, 1, @dfv, @title, N'bin');
            """, conn, tx) { CommandTimeout = 60 };

            cmd.Parameters.Add("@part",  System.Data.SqlDbType.BigInt).Value = part;
            cmd.Parameters.Add("@dfv",   System.Data.SqlDbType.BigInt).Value = dfv;
            cmd.Parameters.Add("@size",  System.Data.SqlDbType.BigInt).Value = payloadSizeBytes;
            cmd.Parameters.Add("@data",  System.Data.SqlDbType.VarBinary, -1).Value = payload;
            cmd.Parameters.Add("@title", System.Data.SqlDbType.NVarChar, 255).Value = $"oversize_{part:D8}";

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await tx.CommitAsync().ConfigureAwait(false);
    }
}
