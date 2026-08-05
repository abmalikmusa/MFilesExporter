using System.Text.Json;
using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Export.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace MFilesExporter.Tests.Export.Metadata;

public class MetadataGeneratorTests : IDisposable
{
    private readonly string _dir;

    public MetadataGeneratorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mfx-mgen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private MetadataOptions Opts() => new()
    {
        OutputDirectory = _dir,
        WriteCsv = true, WriteJson = true, WriteManifest = true,
        FlushEveryNRecords = 1,
    };

    [Fact]
    public async Task ProducesAllThreeArtifacts_AtExpectedPaths()
    {
        var opts = Opts();
        var manifestWriter = new ManifestJsonWriter(opts);
        var gen = new MetadataGenerator(opts, manifestWriter, NullLogger<MetadataGenerator>.Instance);

        await gen.InitializeAsync(default);
        for (var i = 0; i < 5; i++)
        {
            await gen.AppendAsync(MetadataFixtures.Sample(partId: i), default);
        }

        var summary = new ManifestSummary
        {
            JobId          = 7,
            JobName        = "run",
            PartitionKey   = "default",
            SourceServer   = "s",
            SourceDatabase = "d",
            StartedAtUtc   = DateTime.UtcNow.AddMinutes(-5),
            CompletedAtUtc = DateTime.UtcNow,
            Totals         = new ManifestTotals(5, 5, 5, 0, 0, 5000),
            Artifacts      = Array.Empty<ManifestArtifactReference>(),
        };

        var refs = await gen.FinalizeAsync(summary, default);

        File.Exists(Path.Combine(_dir, "metadata.csv")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "metadata.json")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "manifest.json")).Should().BeTrue();

        refs.Should().HaveCount(2);
        refs.All(r => r.RecordCount == 5).Should().BeTrue();
    }

    [Fact]
    public async Task Manifest_ContainsArtifactReferences_WithRecordCounts()
    {
        var opts = Opts();
        var manifestWriter = new ManifestJsonWriter(opts);
        var gen = new MetadataGenerator(opts, manifestWriter, NullLogger<MetadataGenerator>.Instance);

        await gen.InitializeAsync(default);
        await gen.AppendAsync(MetadataFixtures.Sample(partId: 1), default);
        await gen.AppendAsync(MetadataFixtures.Sample(partId: 2), default);

        var summary = MinimalSummary();
        await gen.FinalizeAsync(summary, default);

        var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "manifest.json")));
        var artifacts = manifest.RootElement.GetProperty("artifacts");
        artifacts.GetArrayLength().Should().Be(2);
        foreach (var a in artifacts.EnumerateArray())
        {
            a.GetProperty("recordCount").GetInt64().Should().Be(2);
        }
    }

    [Fact]
    public async Task CsvAndJson_HaveMatchingRecordCounts()
    {
        var opts = Opts();
        var manifestWriter = new ManifestJsonWriter(opts);
        var gen = new MetadataGenerator(opts, manifestWriter, NullLogger<MetadataGenerator>.Instance);

        await gen.InitializeAsync(default);
        for (var i = 0; i < 20; i++)
        {
            await gen.AppendAsync(MetadataFixtures.Sample(partId: i), default);
        }
        await gen.FinalizeAsync(MinimalSummary(), default);

        var csvLines = (await File.ReadAllLinesAsync(Path.Combine(_dir, "metadata.csv")))
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("DocumentPartId"))
            .Count();
        csvLines.Should().Be(20);

        var jsonDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "metadata.json")));
        jsonDoc.RootElement.GetProperty("records").GetArrayLength().Should().Be(20);
    }

    [Fact]
    public async Task WhenOnlyOneFormatEnabled_OnlyThatFormatIsProduced()
    {
        var opts = Opts();
        opts.WriteJson = false;
        opts.WriteManifest = false;

        var manifestWriter = new ManifestJsonWriter(opts);
        var gen = new MetadataGenerator(opts, manifestWriter, NullLogger<MetadataGenerator>.Instance);

        await gen.InitializeAsync(default);
        await gen.AppendAsync(MetadataFixtures.Sample(), default);
        var refs = await gen.FinalizeAsync(MinimalSummary(), default);

        File.Exists(Path.Combine(_dir, "metadata.csv")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "metadata.json")).Should().BeFalse();
        File.Exists(Path.Combine(_dir, "manifest.json")).Should().BeFalse();
        refs.Should().HaveCount(1);
    }

    private static ManifestSummary MinimalSummary() => new()
    {
        JobId = 1, JobName = "t", PartitionKey = "p", SourceServer = "s", SourceDatabase = "d",
        StartedAtUtc = DateTime.UtcNow, CompletedAtUtc = DateTime.UtcNow,
        Totals = new ManifestTotals(0, 0, 0, 0, 0, 0),
        Artifacts = Array.Empty<ManifestArtifactReference>(),
    };
}
