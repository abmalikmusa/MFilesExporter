using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Export.Storage;

namespace MFilesExporter.Tests.Export;

public class PathBuilderTests
{
    [Theory]
    [InlineData("file/with:invalid*chars?.pdf", "file_with_invalid_chars_.pdf")]
    [InlineData("   trim me   ", "trim me")]
    [InlineData(".dotfile", "dotfile")]
    [InlineData("", "untitled")]
    public void SanitizeTitle_HandlesEdgeCases(string title, string expected)
    {
        PathBuilder.SanitizeTitleForFilename(title).Should().Be(expected);
    }

    [Theory]
    [InlineData("PDF", "pdf")]
    [InlineData(".Docx", "docx")]
    [InlineData("x!.z", "xz")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void SanitizeExtension_StripsPunctuation(string? input, string expected)
    {
        PathBuilder.SanitizeExtension(input).Should().Be(expected);
    }

    [Fact]
    public void OutputPath_ShardsByHashPrefix()
    {
        var options = new StorageOptions { RootPath = "/base", ShardDepth = 2, PreserveOriginalFilename = true };
        var builder = new PathBuilder(options);

        var d = new DocumentDescriptor(
            new DocumentFileVersionKey(1, 2),
            new DataFileVersionKey(1, 3),
            "x", "pdf", 0, 0, DateTime.UtcNow);

        var path = builder.BuildOutputPath(d);
        var hash = d.IdempotencyKey.ToHex();
        path.Should().Contain(hash[..2]);
        path.Should().Contain(hash[2..4]);
        path.Should().EndWith(".pdf");
    }
}
