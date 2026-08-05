using System.Text.Json;
using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Export.Metadata;

namespace MFilesExporter.Tests.Export.Metadata;

public class JsonMetadataWriterTests : IDisposable
{
    private readonly string _dir;

    public JsonMetadataWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mfx-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private static MetadataOptions Opts(string dir) => new()
    {
        OutputDirectory = dir,
        WriteCsv = false, WriteJson = true, WriteManifest = false,
        IncludeExtensionFields = true,
        FlushEveryNRecords = 1,
    };

    [Fact]
    public async Task ProducesEnvelope_WithSchemaVersion_AndRecordsArray()
    {
        var opts = Opts(_dir);
        await using var w = new JsonMetadataWriter(opts);
        await w.InitializeAsync(default);
        await w.AppendAsync(MetadataFixtures.Sample(), default);
        await w.AppendAsync(MetadataFixtures.Sample(partId: 2), default);
        await w.FinalizeAsync(default);
        await w.DisposeAsync();

        await using var stream = File.OpenRead(w.OutputPath);
        var doc = await JsonDocument.ParseAsync(stream);

        doc.RootElement.GetProperty("schemaVersion").GetString().Should().Be(MetadataSchema.Version);
        doc.RootElement.GetProperty("schemaId").GetString().Should().Be(MetadataSchema.SchemaId);
        doc.RootElement.GetProperty("generator").GetString().Should().Be(MetadataSchema.GeneratorName);

        var records = doc.RootElement.GetProperty("records");
        records.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Record_ContainsEvery_RequiredField()
    {
        var opts = Opts(_dir);
        await using var w = new JsonMetadataWriter(opts);
        await w.InitializeAsync(default);
        await w.AppendAsync(MetadataFixtures.Sample(), default);
        await w.FinalizeAsync(default);
        await w.DisposeAsync();

        await using var stream = File.OpenRead(w.OutputPath);
        var doc = await JsonDocument.ParseAsync(stream);
        var rec = doc.RootElement.GetProperty("records")[0];

        string[] required =
        {
            "documentPartId", "versionPart", "title", "extension",
            "logicalFileSize", "physicalFileSize", "lastWriteTime",
            "exportPath", "checksum", "exportStatus", "exportDate",
            "workerId", "retryCount",
        };
        foreach (var f in required)
        {
            rec.TryGetProperty(f, out _).Should().BeTrue($"required field '{f}' must appear");
        }

        rec.GetProperty("documentPartId").GetInt64().Should().Be(1);
        rec.GetProperty("title").GetString().Should().Be("Invoice");
        rec.GetProperty("exportStatus").GetString().Should().Be("Succeeded");
    }

    [Fact]
    public async Task ExtensionFields_Included_WhenEnabled()
    {
        var opts = Opts(_dir);
        await using var w = new JsonMetadataWriter(opts);
        await w.InitializeAsync(default);
        await w.AppendAsync(MetadataFixtures.Sample(), default);
        await w.FinalizeAsync(default);
        await w.DisposeAsync();

        await using var stream = File.OpenRead(w.OutputPath);
        var rec = (await JsonDocument.ParseAsync(stream)).RootElement.GetProperty("records")[0];
        rec.GetProperty("idempotencyKey").GetString().Should().Be("abcdef012345");
        rec.GetProperty("dataFileVersionId").GetInt64().Should().Be(999);
    }

    [Fact]
    public async Task ExtensionFields_Omitted_WhenDisabled()
    {
        var opts = Opts(_dir); opts.IncludeExtensionFields = false;
        await using var w = new JsonMetadataWriter(opts);
        await w.InitializeAsync(default);
        await w.AppendAsync(MetadataFixtures.Sample(), default);
        await w.FinalizeAsync(default);
        await w.DisposeAsync();

        await using var stream = File.OpenRead(w.OutputPath);
        var rec = (await JsonDocument.ParseAsync(stream)).RootElement.GetProperty("records")[0];
        rec.TryGetProperty("idempotencyKey", out _).Should().BeFalse();
        rec.TryGetProperty("dataFileVersionId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task DatesAreEmitted_InIso8601Utc()
    {
        var opts = Opts(_dir);
        await using var w = new JsonMetadataWriter(opts);
        await w.InitializeAsync(default);
        await w.AppendAsync(MetadataFixtures.Sample(), default);
        await w.FinalizeAsync(default);
        await w.DisposeAsync();

        await using var stream = File.OpenRead(w.OutputPath);
        var rec = (await JsonDocument.ParseAsync(stream)).RootElement.GetProperty("records")[0];
        rec.GetProperty("lastWriteTime").GetString().Should().EndWith("Z");
        rec.GetProperty("exportDate").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$");
    }
}
