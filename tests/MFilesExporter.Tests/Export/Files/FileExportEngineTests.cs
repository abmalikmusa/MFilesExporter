using System.Text;
using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Export.Files;
using MFilesExporter.Export.Files.Naming;
using MFilesExporter.Export.Files.Strategies;
using Microsoft.Extensions.Logging.Abstractions;

namespace MFilesExporter.Tests.Export.Files;

public class FileExportEngineTests : IDisposable
{
    private readonly string _root;

    public FileExportEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mfx-fileexport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private FileExportEngine Build(FileExportOptions options)
    {
        options.RootPath = _root;
        return new FileExportEngine(
            options,
            FolderStrategyFactory.Create(options),
            new FilenameSanitizer(options),
            DuplicateResolverFactory.Create(options),
            NullLogger<FileExportEngine>.Instance);
    }

    private static FileExportContext Ctx(
        string title = "Invoice",
        string ext = "pdf",
        long partId = 1, long verId = 2, long dfvId = 3,
        DateTime? lastWrite = null) =>
        new()
        {
            Descriptor = new DocumentDescriptor(
                new DocumentFileVersionKey(partId, verId),
                new DataFileVersionKey(partId, dfvId),
                title, ext, 100, 100,
                lastWrite ?? new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc)),
        };

    [Fact]
    public async Task ExportsWithOriginalFilename_UnderFlatStrategy()
    {
        var engine = Build(new FileExportOptions { FolderStrategy = FolderStrategyKind.Flat });
        var payload = Encoding.UTF8.GetBytes("hello");
        using var stream = new MemoryStream(payload);

        var result = await engine.ExportAsync(Ctx(), stream, CancellationToken.None);

        result.FinalFilename.Should().Be("Invoice.pdf");
        result.OutputPath.Should().Be(Path.Combine(_root, "Invoice.pdf"));
        (await File.ReadAllBytesAsync(result.OutputPath)).Should().Equal(payload);
        result.BytesWritten.Should().Be(payload.Length);
        result.DisambiguatedFromDuplicate.Should().BeFalse();
        result.TitleWasSanitized.Should().BeFalse();
    }

    [Fact]
    public async Task ExportsUnderHashShardedFolder()
    {
        var engine = Build(new FileExportOptions
        {
            FolderStrategy = FolderStrategyKind.HashSharded,
            ShardDepth = 2,
        });
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        var ctx = Ctx();

        var result = await engine.ExportAsync(ctx, stream, CancellationToken.None);

        var hex = ctx.Descriptor.IdempotencyKey.ToHex();
        var expectedDir = Path.Combine(_root, hex[..2], hex.Substring(2, 2));
        result.OutputDirectory.Should().Be(expectedDir);
        File.Exists(result.OutputPath).Should().BeTrue();
    }

    [Fact]
    public async Task ExportsUnderDateStrategy_TwentySixEightPath()
    {
        var engine = Build(new FileExportOptions
        {
            FolderStrategy = FolderStrategyKind.Date,
            DateFolderPattern = "yyyy/MM",
        });
        using var stream = new MemoryStream(new byte[]{1});

        var result = await engine.ExportAsync(Ctx(), stream, CancellationToken.None);
        result.OutputDirectory.Should().Be(Path.Combine(_root, "2026", "08"));
    }

    [Fact]
    public async Task DisambiguatesOnCollision_UsingIdempotencyKey()
    {
        var opts = new FileExportOptions
        {
            FolderStrategy = FolderStrategyKind.Flat,
            DuplicateResolution = DuplicateResolutionKind.IdempotencyKeySuffix,
        };
        var engine = Build(opts);

        // Seed a colliding file.
        var target = Path.Combine(_root, "Invoice.pdf");
        await File.WriteAllTextAsync(target, "existing");

        using var stream = new MemoryStream(new byte[]{9,9,9});
        var ctx = Ctx();
        var result = await engine.ExportAsync(ctx, stream, CancellationToken.None);

        result.DisambiguatedFromDuplicate.Should().BeTrue();
        result.FinalFilename.Should().Be($"Invoice_{ctx.Descriptor.IdempotencyKey.ToHex()[..8]}.pdf");
        File.Exists(target).Should().BeTrue("original untouched");
        File.Exists(result.OutputPath).Should().BeTrue();
    }

    [Fact]
    public async Task SanitizesIllegalTitleCharacters()
    {
        var engine = Build(new FileExportOptions { FolderStrategy = FolderStrategyKind.Flat });
        using var stream = new MemoryStream(new byte[]{1});
        var result = await engine.ExportAsync(Ctx(title: "bad/name*here"), stream, CancellationToken.None);

        result.TitleWasSanitized.Should().BeTrue();
        result.FinalFilename.Should().Be("bad_name_here.pdf");
    }

    [Fact]
    public async Task ReservedName_IsPrefixed()
    {
        var engine = Build(new FileExportOptions { FolderStrategy = FolderStrategyKind.Flat });
        using var stream = new MemoryStream(new byte[]{1});
        var result = await engine.ExportAsync(Ctx(title: "CON"), stream, CancellationToken.None);

        result.FinalFilename.Should().Be("_CON.pdf");
        File.Exists(result.OutputPath).Should().BeTrue();
    }

    [Fact]
    public async Task BlankTitle_FallsBackToDefault()
    {
        var engine = Build(new FileExportOptions
        {
            FolderStrategy = FolderStrategyKind.Flat,
            DefaultTitle = "untitled",
        });
        using var stream = new MemoryStream(new byte[]{1});
        var result = await engine.ExportAsync(Ctx(title: ""), stream, CancellationToken.None);

        result.FinalFilename.Should().Be("untitled.pdf");
    }

    [Fact]
    public async Task MissingExtension_FallsBackToDefault()
    {
        var engine = Build(new FileExportOptions
        {
            FolderStrategy = FolderStrategyKind.Flat,
            DefaultExtension = "bin",
        });
        using var stream = new MemoryStream(new byte[]{1});
        var result = await engine.ExportAsync(Ctx(ext: ""), stream, CancellationToken.None);

        result.FinalFilename.Should().Be("Invoice.bin");
    }

    [Fact]
    public async Task UnicodeTitle_Preserved()
    {
        var engine = Build(new FileExportOptions { FolderStrategy = FolderStrategyKind.Flat });
        using var stream = new MemoryStream(new byte[]{1});
        var result = await engine.ExportAsync(Ctx(title: "请款单"), stream, CancellationToken.None);

        result.FinalFilename.Should().Be("请款单.pdf");
        File.Exists(result.OutputPath).Should().BeTrue();
    }

    [Fact]
    public async Task OverlyLongPath_FallsBackToShortHashName()
    {
        // Root is already ~40 chars; a 300-char title definitely overflows.
        var engine = Build(new FileExportOptions
        {
            FolderStrategy = FolderStrategyKind.Flat,
            MaxFullPathLength = 100,
            MaxFilenameLength = 500,       // let sanitizer keep long title
        });
        var longTitle = new string('x', 300);
        using var stream = new MemoryStream(new byte[]{1});
        var result = await engine.ExportAsync(Ctx(title: longTitle), stream, CancellationToken.None);

        result.RequiredLongPathPrefix.Should().BeTrue();
        // 16 hex chars + ".pdf"
        result.FinalFilename.Length.Should().Be(20);
        File.Exists(result.OutputPath).Should().BeTrue();
    }
}
