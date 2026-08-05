using FluentAssertions;
using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Tests.Domain;

public class DocumentFileVersionKeyTests
{
    [Fact]
    public void Ordering_IsLexicographic()
    {
        var a = new DocumentFileVersionKey(1, 100);
        var b = new DocumentFileVersionKey(1, 101);
        var c = new DocumentFileVersionKey(2, 0);
        (a < b).Should().BeTrue();
        (b < c).Should().BeTrue();
    }

    [Fact]
    public void Origin_IsSmallest()
    {
        (DocumentFileVersionKey.Origin <= new DocumentFileVersionKey(long.MinValue + 1, 0)).Should().BeTrue();
    }
}
