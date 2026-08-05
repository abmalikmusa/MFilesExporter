using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Export.Files;
using MFilesExporter.Export.Files.Strategies;

namespace MFilesExporter.Tests.Export.Files;

public class FolderStrategyTests
{
    private static FileExportContext Context(long partId = 1, long verId = 1, string ext = "pdf") =>
        new()
        {
            Descriptor = new DocumentDescriptor(
                new DocumentFileVersionKey(partId, verId),
                new DataFileVersionKey(partId, verId),
                "Invoice", ext, 100, 100,
                new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc)),
        };

    [Fact]
    public void Flat_ReturnsEmpty()
    {
        new FlatFolderStrategy().BuildRelativeFolder(Context()).Should().BeEmpty();
    }

    [Fact]
    public void HashSharded_Depth2_Yields_TwoTwoCharSegments()
    {
        var s = new HashShardedFolderStrategy(2);
        var folder = s.BuildRelativeFolder(Context());

        var parts = folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        parts.Should().HaveCount(2);
        parts[0].Should().HaveLength(2);
        parts[1].Should().HaveLength(2);
    }

    [Fact]
    public void HashSharded_IsDeterministic()
    {
        var s = new HashShardedFolderStrategy(2);
        var a = s.BuildRelativeFolder(Context(10, 20));
        var b = s.BuildRelativeFolder(Context(10, 20));
        a.Should().Be(b);
    }

    [Theory]
    [InlineData(-3, 1000)]
    [InlineData(0, 1000)]
    [InlineData(long.MaxValue, 512)]
    public void NumericShard_AlwaysReturnsNonNegativeBucket(long partId, int bucketCount)
    {
        var s = new NumericShardFolderStrategy(bucketCount);
        var folder = s.BuildRelativeFolder(Context(partId));
        int bucket = int.Parse(folder, System.Globalization.CultureInfo.InvariantCulture);
        bucket.Should().BeInRange(0, bucketCount - 1);
    }

    [Fact]
    public void Date_UsesConfiguredPattern()
    {
        var s = new DateFolderStrategy("yyyy/MM");
        var folder = s.BuildRelativeFolder(Context());
        folder.Should().Be(Path.Combine("2026", "08"));
    }

    [Fact]
    public void Category_UsesExtension_WhenNoCategoryProvided()
    {
        var s = new CategoryFolderStrategy();
        s.BuildRelativeFolder(Context(ext: "pdf")).Should().Be("pdfs");
    }

    [Fact]
    public void Category_UsesExplicitCategory_WhenProvided()
    {
        var s = new CategoryFolderStrategy();
        var ctx = Context() with { Category = "invoice" };
        s.BuildRelativeFolder(ctx).Should().Be("invoices");
    }

    [Fact]
    public void Category_FallsBackToMisc_WhenBoth_Are_Empty()
    {
        var s = new CategoryFolderStrategy();
        var ctx = Context(ext: "");
        s.BuildRelativeFolder(ctx).Should().Be("misc");
    }

    [Fact]
    public void ShardedByDate_ComposesShardAndDate()
    {
        var s = new ShardedByDateFolderStrategy(2, "yyyy/MM");
        var folder = s.BuildRelativeFolder(Context());

        var parts = folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        parts.Should().HaveCount(4, "2 shard segments + 2 date segments");
        parts[0].Should().HaveLength(2);
        parts[1].Should().HaveLength(2);
        parts[2].Should().Be("2026");
        parts[3].Should().Be("08");
    }

    [Fact]
    public void Factory_MaterializesEveryKind()
    {
        foreach (FolderStrategyKind kind in Enum.GetValues<FolderStrategyKind>())
        {
            var opts = new FileExportOptions { FolderStrategy = kind };
            var s = FolderStrategyFactory.Create(opts);
            s.Kind.Should().Be(kind);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void HashSharded_RejectsInvalidDepth(int depth)
    {
        Action act = () => new HashShardedFolderStrategy(depth);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
