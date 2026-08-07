using System.Security.Cryptography;
using FluentAssertions;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Domain.Documents;
using MFilesExporter.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace MFilesExporter.IntegrationTests.EndToEnd;

/// <summary>
/// Proves that a cancelled export resumes cleanly from its persisted
/// checkpoint. The single non-negotiable property of a 5-million-document
/// batch job: if it dies, the second run must skip the work the first run
/// already committed.
/// </summary>
[Collection("SqlServer")]
public sealed class ResumeAfterCancellationTests
{
    // A larger corpus + single-worker phase 1 give us enough headroom that
    // the wait-for-N-files → cancel loop reliably interrupts mid-run rather
    // than racing the whole pipeline to completion.
    private const int CorpusSize        = 500;
    private const int InterruptAfterN   = 30;
    private const string Partition      = "resume";

    private readonly SqlServerFixture _sql;

    public ResumeAfterCancellationTests(SqlServerFixture sql) => _sql = sql;

    [Fact]
    public async Task ResumesFromCheckpoint_AfterMidRunCancellation()
    {
        // -----------------------------------------------------------------
        // Clear the shared vault and seed a private ID range.
        // -----------------------------------------------------------------
        await VaultSeeder.ResetAsync(_sql.SourceConnectionString).ConfigureAwait(false);
        var seeded = await VaultSeeder.SeedAsync(
            _sql.SourceConnectionString,
            CorpusSize,
            seed: 1_337,
            partStartId: 2_000_000L,
            dfvStartId: 6_000_000L).ConfigureAwait(false);

        // Shared output root spans both runs — the state store (SQLite),
        // checkpoint WAL, and partial documents all live here and carry over.
        var outputRoot = Path.Combine(
            Path.GetTempPath(), "mfilesexporter-resume-" + Guid.NewGuid().ToString("N"));
        try
        {
            // -------------------------------------------------------------
            // Phase 1 — start the pipeline, watch for InterruptAfterN files,
            // then cancel. Snapshot the checkpoint + on-disk count.
            // -------------------------------------------------------------
            DocumentFileVersionKey checkpoint1;
            int filesAtInterrupt;

            {
                await using var host = ExporterTestHost.CreateSharing(_sql, outputRoot, workerCount: 1, partitionKey: Partition);
                await host.Services.GetRequiredService<IExportStateStore>()
                    .InitializeAsync(CancellationToken.None).ConfigureAwait(false);

                using var cts = new CancellationTokenSource();
                var pipelineTask = host.Pipeline.RunAsync(cts.Token);

                var documentsDir = Path.Combine(outputRoot, "documents");
                await WaitForFileCountAsync(documentsDir, InterruptAfterN, TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

                // Give the checkpoint cadence one flush interval to persist
                // the observed position — the WAL flushes every 500 ms and
                // outcome batches every 500 ms in this config.
                await Task.Delay(TimeSpan.FromMilliseconds(600)).ConfigureAwait(false);
                cts.Cancel();

                try { await pipelineTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }

                var store = host.Services.GetRequiredService<IExportStateStore>();
                checkpoint1 = await store.GetCheckpointAsync(Partition, CancellationToken.None)
                    .ConfigureAwait(false);
                checkpoint1.Should().NotBe(DocumentFileVersionKey.Origin,
                    "the pipeline must persist a checkpoint before being cancelled");

                filesAtInterrupt = CountFiles(documentsDir);
                filesAtInterrupt.Should().BeGreaterThanOrEqualTo(InterruptAfterN)
                    .And.BeLessThan(CorpusSize,
                        "we must interrupt mid-run, not at the end");
            }

            // -------------------------------------------------------------
            // Phase 2 — fresh host, same paths. Prove:
            //   1) the second run picks up from checkpoint1, not from origin
            //   2) every document ends up on disk exactly once
            //   3) checkpoint advances monotonically
            // -------------------------------------------------------------
            {
                await using var host = ExporterTestHost.CreateSharing(_sql, outputRoot, workerCount: 4, partitionKey: Partition);
                await host.Services.GetRequiredService<IExportStateStore>()
                    .InitializeAsync(CancellationToken.None).ConfigureAwait(false);

                // Assert the checkpoint survived the process boundary before
                // we run anything — that's the point of persistence.
                var storeBefore = host.Services.GetRequiredService<IExportStateStore>();
                var restoredCheckpoint = await storeBefore
                    .GetCheckpointAsync(Partition, CancellationToken.None).ConfigureAwait(false);
                restoredCheckpoint.Should().Be(checkpoint1,
                    "phase 2's host must observe phase 1's persisted checkpoint");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await host.Pipeline.RunAsync(cts.Token).ConfigureAwait(false);

                var checkpoint2 = await storeBefore.GetCheckpointAsync(Partition, CancellationToken.None)
                    .ConfigureAwait(false);
                checkpoint2.Should().BeGreaterThan(checkpoint1,
                    "checkpoint must advance monotonically across a resume");

                // Every seeded document present, exactly once, with matching checksum.
                var documentsDir = Path.Combine(outputRoot, "documents");
                var writtenFiles = Directory.EnumerateFiles(documentsDir, "*", SearchOption.AllDirectories)
                    .ToList();
                writtenFiles.Should().HaveCount(CorpusSize,
                    "every document must exist exactly once after the resumed run");

                var writtenByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in writtenFiles)
                {
                    var hex = ComputeSha256Hex(await File.ReadAllBytesAsync(file).ConfigureAwait(false));
                    writtenByHash[hex] = file;
                }
                foreach (var doc in seeded)
                {
                    writtenByHash.Should().ContainKey(doc.ExpectedSha256Hex,
                        $"resumed run should have written {doc.Title}.{doc.Extension}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static async Task WaitForFileCountAsync(string dir, int minimum, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (CountFiles(dir) >= minimum) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Expected {minimum} files under {dir} within {timeout}, saw {CountFiles(dir)}.");
    }

    private static int CountFiles(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count()
            : 0;

    private static string ComputeSha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
