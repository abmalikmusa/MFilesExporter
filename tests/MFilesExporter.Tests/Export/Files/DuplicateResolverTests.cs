using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Export.Files.Naming;

namespace MFilesExporter.Tests.Export.Files;

public class DuplicateResolverTests : IDisposable
{
    private readonly string _root;

    public DuplicateResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mfx-dupres-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private static DocumentDescriptor Descriptor(long p = 1, long v = 1, long d = 1) =>
        new(new DocumentFileVersionKey(p, v), new DataFileVersionKey(p, d),
            "Invoice", "pdf", 100, 100, DateTime.UtcNow);

    [Fact]
    public void IdempotencyKey_UntouchedWhenNoCollision()
    {
        var target = Path.Combine(_root, "Invoice.pdf");
        var r = new IdempotencyKeySuffixResolver().Resolve(target, Descriptor());
        r.Should().Be(target);
    }

    [Fact]
    public void IdempotencyKey_AppendsHash_OnCollision()
    {
        var target = Path.Combine(_root, "Invoice.pdf");
        File.WriteAllText(target, "existing");

        var desc = Descriptor(1, 2, 3);
        var expectedSuffix = desc.IdempotencyKey.ToHex()[..8];

        var r = new IdempotencyKeySuffixResolver().Resolve(target, desc);
        r.Should().Be(Path.Combine(_root, $"Invoice_{expectedSuffix}.pdf"));
    }

    [Fact]
    public void IdempotencyKey_IsDeterministic_ForSameDocument()
    {
        var target = Path.Combine(_root, "same.pdf");
        File.WriteAllText(target, "existing");

        var desc = Descriptor(42, 43, 44);
        var a = new IdempotencyKeySuffixResolver().Resolve(target, desc);
        var b = new IdempotencyKeySuffixResolver().Resolve(target, desc);

        a.Should().Be(b);
    }

    [Fact]
    public void CounterSuffix_IncrementsUntilFree()
    {
        var target = Path.Combine(_root, "doc.pdf");
        File.WriteAllText(target, "0");
        File.WriteAllText(Path.Combine(_root, "doc (1).pdf"), "1");
        File.WriteAllText(Path.Combine(_root, "doc (2).pdf"), "2");

        var r = new CounterSuffixResolver().Resolve(target, Descriptor());
        r.Should().Be(Path.Combine(_root, "doc (3).pdf"));
    }

    [Fact]
    public void FailOnCollision_ThrowsWhenPresent()
    {
        var target = Path.Combine(_root, "clash.pdf");
        File.WriteAllText(target, "x");

        Action act = () => new FailOnCollisionResolver().Resolve(target, Descriptor());
        act.Should().Throw<IOException>();
    }

    [Fact]
    public void Overwrite_ReturnsSamePath()
    {
        var target = Path.Combine(_root, "same.pdf");
        File.WriteAllText(target, "existing");
        new OverwriteResolver().Resolve(target, Descriptor()).Should().Be(target);
    }

    [Fact]
    public void Factory_MaterializesEveryKind()
    {
        foreach (DuplicateResolutionKind kind in Enum.GetValues<DuplicateResolutionKind>())
        {
            var opts = new FileExportOptions { DuplicateResolution = kind };
            var r = DuplicateResolverFactory.Create(opts);
            r.Kind.Should().Be(kind);
        }
    }
}
