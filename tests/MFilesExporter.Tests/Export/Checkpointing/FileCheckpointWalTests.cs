using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Export.Checkpointing.WriteAheadLog;
using Microsoft.Extensions.Logging.Abstractions;

namespace MFilesExporter.Tests.Export.Checkpointing;

public class FileCheckpointWalTests : IDisposable
{
    private readonly string _dir;

    public FileCheckpointWalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mfx-wal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private FileCheckpointWal NewWal() =>
        new(new CheckpointOptions { WalDirectory = _dir },
            NullLogger<FileCheckpointWal>.Instance);

    private static WalEntry Entry(long part, long ver, long docs, DateTimeOffset? at = null) =>
        new(new DocumentFileVersionKey(part, ver), docs, at ?? new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ReadLatest_ReturnsNull_WhenFileMissing()
    {
        var wal = NewWal();
        var read = await wal.ReadLatestAsync(1, "p", default);
        read.Should().BeNull();
    }

    [Fact]
    public async Task AppendThenRead_RoundTrips()
    {
        var wal = NewWal();
        var entry = Entry(10, 20, 500);
        await wal.AppendAsync(1, "p", entry, default);

        var read = await wal.ReadLatestAsync(1, "p", default);
        read.Should().NotBeNull();
        read!.Cursor.Should().Be(entry.Cursor);
        read.DocumentsProcessedInPartition.Should().Be(entry.DocumentsProcessedInPartition);
        read.PersistedAtUtc.Should().Be(entry.PersistedAtUtc);
    }

    [Fact]
    public async Task Append_Overwrites_PreviousSlot()
    {
        var wal = NewWal();
        await wal.AppendAsync(1, "p", Entry(1, 1, 10), default);
        await wal.AppendAsync(1, "p", Entry(2, 3, 100), default);
        await wal.AppendAsync(1, "p", Entry(4, 5, 500), default);

        var read = await wal.ReadLatestAsync(1, "p", default);
        read!.Cursor.Should().Be(new DocumentFileVersionKey(4, 5));
        read.DocumentsProcessedInPartition.Should().Be(500);
    }

    [Fact]
    public async Task CorruptedLine_IsRejected()
    {
        var wal = NewWal();
        await wal.AppendAsync(1, "p", Entry(1, 1, 10), default);

        // Corrupt the file.
        var path = Directory.GetFiles(_dir, "*.wal").Single();
        var lines = File.ReadAllText(path).Trim();
        File.WriteAllText(path, lines.Replace("1|1", "999|999"));  // breaks CRC

        var read = await wal.ReadLatestAsync(1, "p", default);
        read.Should().BeNull("mismatched CRC must reject the line");
    }

    [Fact]
    public async Task EmptyFile_ReadsAsNull()
    {
        var wal = NewWal();
        await wal.AppendAsync(1, "p", Entry(1, 1, 10), default);
        var path = Directory.GetFiles(_dir, "*.wal").Single();
        File.WriteAllText(path, string.Empty);

        var read = await wal.ReadLatestAsync(1, "p", default);
        read.Should().BeNull();
    }

    [Fact]
    public async Task DifferentPartitions_UseSeparateFiles()
    {
        var wal = NewWal();
        await wal.AppendAsync(1, "alpha",   Entry(1, 1, 100), default);
        await wal.AppendAsync(1, "bravo",   Entry(2, 2, 200), default);

        var a = await wal.ReadLatestAsync(1, "alpha", default);
        var b = await wal.ReadLatestAsync(1, "bravo", default);

        a!.Cursor.Should().Be(new DocumentFileVersionKey(1, 1));
        b!.Cursor.Should().Be(new DocumentFileVersionKey(2, 2));
    }

    [Fact]
    public async Task NoStrayTempFiles_AfterAppend()
    {
        var wal = NewWal();
        await wal.AppendAsync(1, "p", Entry(1, 1, 10), default);
        var tempFiles = Directory.EnumerateFiles(_dir, "*.tmp").ToArray();
        tempFiles.Should().BeEmpty("atomic-swap must leave no .tmp files");
    }

    [Fact]
    public void TryDeserializeLine_RejectsMalformedInputs()
    {
        var ok = FileCheckpointWal.TryDeserializeLine("not|enough|fields", out var e1);
        ok.Should().BeFalse(); e1.Should().BeNull();

        ok = FileCheckpointWal.TryDeserializeLine("", out var e2);
        ok.Should().BeFalse(); e2.Should().BeNull();
    }
}
