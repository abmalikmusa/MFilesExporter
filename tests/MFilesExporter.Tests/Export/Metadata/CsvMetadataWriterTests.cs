using System.Text;
using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Export.Metadata;

namespace MFilesExporter.Tests.Export.Metadata;

public class CsvMetadataWriterTests : IDisposable
{
    private readonly string _dir;

    public CsvMetadataWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mfx-csv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private static MetadataOptions Opts(string dir) => new()
    {
        OutputDirectory = dir,
        WriteCsv = true, WriteJson = false, WriteManifest = false,
        CsvIncludeHeader = true,
        CsvIncludeUtf8Bom = true,
        IncludeExtensionFields = true,
        FlushEveryNRecords = 1,
    };

    [Fact]
    public async Task WritesHeader_Bom_AndRow()
    {
        var opts = Opts(_dir);
        await using var w = new CsvMetadataWriter(opts);
        await w.InitializeAsync(default);
        await w.AppendAsync(MetadataFixtures.Sample(), default);
        await w.FinalizeAsync(default);
        await w.DisposeAsync();

        var raw = await File.ReadAllBytesAsync(w.OutputPath);
        // BOM check
        raw[0].Should().Be(0xEF); raw[1].Should().Be(0xBB); raw[2].Should().Be(0xBF);

        var text = Encoding.UTF8.GetString(raw.AsSpan(3));    // skip BOM
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[0].Should().StartWith("DocumentPartId,VersionPart,Title,Extension");
        lines[0].Should().EndWith("RetryCount,IdempotencyKey,DataFileVersionId");
        lines[1].Should().Contain("Invoice");
        lines[1].Should().Contain("pdf");
        lines[1].Should().Contain("Succeeded");
    }

    [Fact]
    public async Task EscapesCommas_QuotesAndNewlines_InTitle()
    {
        var opts = Opts(_dir);
        await using var w = new CsvMetadataWriter(opts);
        await w.InitializeAsync(default);
        await w.AppendAsync(MetadataFixtures.Sample(title: "he said \"hi, world\"\nnext line"), default);
        await w.FinalizeAsync(default);
        await w.DisposeAsync();

        var content = await File.ReadAllTextAsync(w.OutputPath, Encoding.UTF8);
        // The title must be quoted, with doubled interior quote and preserved comma/newline.
        content.Should().Contain("\"he said \"\"hi, world\"\"\nnext line\"");
    }

    [Fact]
    public async Task Unicode_Preserved_InUtf8()
    {
        var opts = Opts(_dir);
        await using var w = new CsvMetadataWriter(opts);
        await w.InitializeAsync(default);
        await w.AppendAsync(MetadataFixtures.Sample(title: "请款单"), default);
        await w.FinalizeAsync(default);
        await w.DisposeAsync();

        var content = await File.ReadAllTextAsync(w.OutputPath, Encoding.UTF8);
        content.Should().Contain("请款单");
    }

    [Fact]
    public async Task NoHeader_WhenDisabled()
    {
        var opts = Opts(_dir);
        opts.CsvIncludeHeader = false;
        await using var w = new CsvMetadataWriter(opts);
        await w.InitializeAsync(default);
        await w.AppendAsync(MetadataFixtures.Sample(), default);
        await w.FinalizeAsync(default);
        await w.DisposeAsync();

        var text = await File.ReadAllTextAsync(w.OutputPath, Encoding.UTF8);
        text.Should().NotContain("DocumentPartId");
        text.Trim().Split("\r\n").Should().HaveCount(1);
    }

    [Fact]
    public async Task Records_AreCounted()
    {
        var opts = Opts(_dir);
        await using var w = new CsvMetadataWriter(opts);
        await w.InitializeAsync(default);
        for (var i = 0; i < 10; i++)
        {
            await w.AppendAsync(MetadataFixtures.Sample(partId: i), default);
        }
        await w.FinalizeAsync(default);

        w.RecordCount.Should().Be(10);
    }

    [Fact]
    public async Task ConcurrentAppends_AreSerialised()
    {
        var opts = Opts(_dir);
        await using var w = new CsvMetadataWriter(opts);
        await w.InitializeAsync(default);

        var tasks = new List<Task>();
        for (var i = 0; i < 100; i++)
        {
            int id = i;
            tasks.Add(Task.Run(() => w.AppendAsync(MetadataFixtures.Sample(partId: id), default)));
        }
        await Task.WhenAll(tasks);
        await w.FinalizeAsync(default);

        w.RecordCount.Should().Be(100);
        var lines = (await File.ReadAllLinesAsync(w.OutputPath, Encoding.UTF8))
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("DocumentPartId"))
            .ToArray();
        lines.Should().HaveCount(100);
    }
}
