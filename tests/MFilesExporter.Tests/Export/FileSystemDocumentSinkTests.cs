using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Export.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MFilesExporter.Tests.Export;

public class FileSystemDocumentSinkTests : IAsyncLifetime
{
    private string _root = string.Empty;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mfx-sink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Write_ProducesShardedFile_AndCorrectChecksum()
    {
        var options = new StorageOptions
        {
            RootPath = Path.Combine(_root, "documents"),
            ManifestPath = Path.Combine(_root, "manifests"),
            ShardDepth = 2,
            WriteBufferSize = 4096,
            PreserveOriginalFilename = true,
        };
        var sink = new FileSystemDocumentSink(
            new PathBuilder(options),
            new Sha256ChecksumCalculatorFactory(),
            options,
            NullLogger<FileSystemDocumentSink>.Instance);

        var descriptor = new DocumentDescriptor(
            new DocumentFileVersionKey(123, 4),
            new DataFileVersionKey(123, 456),
            "sample document", "pdf", 11, 11, DateTime.UtcNow);

        var payload = Encoding.UTF8.GetBytes("hello world");
        using var ms = new MemoryStream(payload);

        var result = await sink.WriteAsync(descriptor, ms, CancellationToken.None);

        File.Exists(result.OutputPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(result.OutputPath)).Should().BeEquivalentTo(payload);

        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        result.ChecksumHex.Should().Be(expected);
        result.BytesWritten.Should().Be(payload.Length);
    }
}
