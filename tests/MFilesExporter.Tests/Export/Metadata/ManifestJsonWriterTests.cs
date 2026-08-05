using System.Text.Json;
using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Export.Metadata;

namespace MFilesExporter.Tests.Export.Metadata;

public class ManifestJsonWriterTests : IDisposable
{
    private readonly string _dir;

    public ManifestJsonWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mfx-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task WritesFull_Manifest_WithAllBlocks()
    {
        var opts = new MetadataOptions { OutputDirectory = _dir };
        var writer = new ManifestJsonWriter(opts);

        var summary = new ManifestSummary
        {
            JobId          = 42,
            JobName        = "monthly-export",
            PartitionKey   = "default",
            SourceServer   = "mfiles-sql-01",
            SourceDatabase = "MFilesVault",
            StartedAtUtc   = new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 8, 3, 18, 0, 0, DateTimeKind.Utc),
            Totals         = new ManifestTotals(
                DocumentsExpected: 5_041_559,
                DocumentsRecorded: 5_041_500,
                Succeeded:         5_041_000,
                Failed:            400,
                Skipped:           100,
                TotalBytesWritten: 10_000_000_000L),
            Artifacts = new[]
            {
                new ManifestArtifactReference("metadata.csv",  "csv",  5_041_500),
                new ManifestArtifactReference("metadata.json", "json", 5_041_500),
            },
        };

        var path = await writer.WriteAsync(summary, default);
        File.Exists(path).Should().BeTrue();

        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = doc.RootElement;

        root.GetProperty("schemaVersion").GetString().Should().Be(MetadataSchema.Version);
        root.GetProperty("generator").GetString().Should().Be(MetadataSchema.GeneratorName);

        var job = root.GetProperty("job");
        job.GetProperty("id").GetInt64().Should().Be(42);
        job.GetProperty("name").GetString().Should().Be("monthly-export");
        job.GetProperty("partitionKey").GetString().Should().Be("default");

        var totals = root.GetProperty("totals");
        totals.GetProperty("documentsExpected").GetInt64().Should().Be(5_041_559);
        totals.GetProperty("succeeded").GetInt64().Should().Be(5_041_000);

        var artifacts = root.GetProperty("artifacts");
        artifacts.GetArrayLength().Should().Be(2);
        artifacts[0].GetProperty("format").GetString().Should().Be("csv");
        artifacts[1].GetProperty("format").GetString().Should().Be("json");
    }

    [Fact]
    public async Task Writes_Atomically_ViaTempThenRename()
    {
        var opts = new MetadataOptions { OutputDirectory = _dir };
        var writer = new ManifestJsonWriter(opts);

        var summary = MakeMinimalSummary();
        var path = await writer.WriteAsync(summary, default);

        // Only the final file should exist; no stray .partial left over.
        Directory.EnumerateFiles(_dir, "*.partial").Should().BeEmpty();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task CompletedAtUtc_Null_IsPreserved()
    {
        var opts = new MetadataOptions { OutputDirectory = _dir };
        var writer = new ManifestJsonWriter(opts);

        var summary = MakeMinimalSummary() with { CompletedAtUtc = null };
        var path = await writer.WriteAsync(summary, default);

        var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        doc.RootElement.GetProperty("job").GetProperty("completedAtUtc").ValueKind
            .Should().Be(JsonValueKind.Null);
    }

    private static ManifestSummary MakeMinimalSummary() => new()
    {
        JobId          = 1,
        JobName        = "test",
        PartitionKey   = "p",
        SourceServer   = "s",
        SourceDatabase = "d",
        StartedAtUtc   = DateTime.UtcNow,
        CompletedAtUtc = DateTime.UtcNow,
        Totals         = new ManifestTotals(0, 0, 0, 0, 0, 0),
        Artifacts      = Array.Empty<ManifestArtifactReference>(),
    };
}
