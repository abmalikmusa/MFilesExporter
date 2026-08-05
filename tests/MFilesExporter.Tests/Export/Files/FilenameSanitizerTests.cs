using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Export.Files.Naming;

namespace MFilesExporter.Tests.Export.Files;

public class FilenameSanitizerTests
{
    private static FilenameSanitizer Create(FileExportOptions? opts = null) =>
        new(opts ?? new FileExportOptions());

    [Theory]
    [InlineData("Invoice", "pdf",     "Invoice.pdf",          false)]
    [InlineData("Invoice.pdf", "pdf", "Invoice.pdf.pdf",      false)]   // title carries dot
    public void HappyPath(string title, string ext, string expected, bool sanitized)
    {
        Create().Sanitize(title, ext, out var was).Should().Be(expected);
        was.Should().Be(sanitized);
    }

    [Theory]
    [InlineData("bad/name\\here:*?\"<>|", "pdf", "bad_name_here_______.pdf")]
    [InlineData("with\0null", "txt", "withnull.txt")]           // control char stripped
    [InlineData("tabby\there", "txt", "tabbyhere.txt")]         // \t is a control char
    public void IllegalCharacters_ReplacedOrStripped(string title, string ext, string expected)
    {
        Create().Sanitize(title, ext, out var was).Should().Be(expected);
        was.Should().BeTrue();
    }

    [Theory]
    [InlineData("trailing dots...   ", "pdf", "trailing dots.pdf")]
    [InlineData("   .  ", "pdf", "untitled.pdf")]
    public void TrailingDotsAndSpaces_Trimmed(string title, string ext, string expected)
    {
        Create().Sanitize(title, ext, out var was).Should().Be(expected);
        was.Should().BeTrue();
    }

    [Theory]
    [InlineData("CON", "pdf", "_CON.pdf")]
    [InlineData("aux", "txt", "_aux.txt")]
    [InlineData("COM1", "log", "_COM1.log")]
    [InlineData("lpt9", "dat", "_lpt9.dat")]
    public void ReservedWindowsNames_ArePrefixed(string title, string ext, string expected)
    {
        Create().Sanitize(title, ext, out var was).Should().Be(expected);
        was.Should().BeTrue();
    }

    [Fact]
    public void EmptyTitle_UsesDefault()
    {
        Create().Sanitize("", "pdf", out var was).Should().Be("untitled.pdf");
        was.Should().BeTrue();
    }

    [Fact]
    public void NullTitle_UsesDefault()
    {
        Create().Sanitize(null, "pdf", out var _).Should().Be("untitled.pdf");
    }

    [Fact]
    public void EmptyExtension_UsesDefault()
    {
        Create().Sanitize("Invoice", "", out var _).Should().Be("Invoice.bin");
    }

    [Fact]
    public void EmptyExtension_And_EmptyDefault_ProducesExtensionlessFile()
    {
        var opts = new FileExportOptions { DefaultExtension = string.Empty };
        Create(opts).Sanitize("Invoice", "", out var _).Should().Be("Invoice");
    }

    [Fact]
    public void UnicodeIsNormalizedToNfc()
    {
        // "é" as combining ("e" + "◌́" U+0301) should normalize to composed form.
        var combining = "cafe\u0301";
        var result = Create().Sanitize(combining, "txt", out var was);
        result.Should().Be("café.txt");
        was.Should().BeTrue();
    }

    [Fact]
    public void UnicodeContent_IsPreserved()
    {
        Create().Sanitize("请款单", "pdf", out var was).Should().Be("请款单.pdf");
        was.Should().BeFalse();
    }

    [Fact]
    public void OverlyLongTitle_IsTruncated_LeavingRoomForExtension()
    {
        var opts = new FileExportOptions { MaxFilenameLength = 20 };
        var name = new string('x', 100);
        var result = Create(opts).Sanitize(name, "pdf", out var was);
        result.Length.Should().Be(20);
        result.Should().EndWith(".pdf");
        was.Should().BeTrue();
    }

    [Theory]
    [InlineData("PDF", "pdf")]
    [InlineData(".DocX", "docx")]
    [InlineData("t x t", "txt")]           // internal spaces stripped
    [InlineData("weird!", "weird")]
    public void Extensions_AreNormalised(string input, string expected)
    {
        var name = Create().Sanitize("x", input, out var _);
        name.Should().EndWith("." + expected);
    }
}
