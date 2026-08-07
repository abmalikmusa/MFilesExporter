using System.Security.Cryptography;
using FluentAssertions;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace MFilesExporter.IntegrationTests.EndToEnd;

/// <summary>
/// End-to-end proof that the pipeline can move a small corpus from a real
/// SQL Server-backed vault to disk, verify checksums, and update the tracking
/// database. This is the test the design docs have been claiming for weeks —
/// nothing about the architecture is trusted until this passes.
/// </summary>
[Collection("SqlServer")]
public sealed class ExportSmallCorpusTests
{
    private const int CorpusSize = 100;

    private readonly SqlServerFixture _sql;

    public ExportSmallCorpusTests(SqlServerFixture sql) => _sql = sql;

    [Fact]
    public async Task ExportsAllDocuments_WithMatchingChecksums()
    {
        // -----------------------------------------------------------------
        // Arrange — clear the vault (shared collection), seed our corpus,
        // initialize the state store, start the host.
        // -----------------------------------------------------------------
        await VaultSeeder.ResetAsync(_sql.SourceConnectionString).ConfigureAwait(false);
        var seeded = await VaultSeeder.SeedAsync(_sql.SourceConnectionString, CorpusSize)
            .ConfigureAwait(false);

        await using var host = ExporterTestHost.Create(_sql, workerCount: 4);
        await host.Services.GetRequiredService<IExportStateStore>()
            .InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        // -----------------------------------------------------------------
        // Act — run the pipeline. Cap the run at 60 seconds; a 100-doc
        // corpus should finish in far less on any modern host.
        // -----------------------------------------------------------------
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await host.Pipeline.RunAsync(cts.Token).ConfigureAwait(false);

        // -----------------------------------------------------------------
        // Assert — every seeded document has a matching file on disk.
        // -----------------------------------------------------------------
        var documentsRoot = Path.Combine(host.OutputRoot, "documents");
        var writtenFiles  = Directory.EnumerateFiles(documentsRoot, "*", SearchOption.AllDirectories)
                                     .ToList();

        writtenFiles.Should().HaveCount(CorpusSize,
            "every seeded document should appear exactly once under the sink root");

        // Build a lookup by SHA-256 so we can match files to expected payloads
        // regardless of the folder-shard layout the sink chose.
        var writtenByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in writtenFiles)
        {
            var hex = ComputeSha256Hex(await File.ReadAllBytesAsync(file).ConfigureAwait(false));
            writtenByHash[hex] = file;
        }

        foreach (var doc in seeded)
        {
            writtenByHash.Should().ContainKey(doc.ExpectedSha256Hex,
                $"seeded document {doc.Title}.{doc.Extension} was expected on disk with checksum {doc.ExpectedSha256Hex[..12]}…");
        }

        // -----------------------------------------------------------------
        // Assert — tracking DB counters reconcile with the corpus.
        // -----------------------------------------------------------------
        var store    = host.Services.GetRequiredService<IExportStateStore>();
        var counters = await store.GetCountersAsync(CancellationToken.None).ConfigureAwait(false);

        counters.TotalRecorded.Should().Be(CorpusSize);
        counters.TotalSucceeded.Should().Be(CorpusSize);
        counters.TotalFailed.Should().Be(0);
        counters.TotalSkipped.Should().Be(0);
        counters.TotalBytesWritten.Should().Be(seeded.Sum(d => (long)d.Payload.Length));
    }

    private static string ComputeSha256Hex(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
