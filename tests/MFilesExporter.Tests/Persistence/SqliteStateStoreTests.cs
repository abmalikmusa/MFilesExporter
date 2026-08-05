using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Persistence.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace MFilesExporter.Tests.Persistence;

public class SqliteStateStoreTests : IAsyncLifetime
{
    private string _path = string.Empty;
    private SqliteStateStore _store = null!;

    public async Task InitializeAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"mfx-state-{Guid.NewGuid():N}.db");
        _store = new SqliteStateStore(
            new StateStoreOptions { ConnectionString = _path, CacheSizeKib = 4096, EnableMemoryMappedIo = false },
            NullLogger<SqliteStateStore>.Instance);
        await _store.InitializeAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var f = _path + suffix;
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    [Fact]
    public async Task RecordOutcome_IsIdempotent()
    {
        var key = IdempotencyKey.For(1, 1, 1);
        var o = new ExportOutcome
        {
            IdempotencyKey = key,
            DocumentFileVersionKey = new DocumentFileVersionKey(1, 1),
            DataFileVersionKey = new DataFileVersionKey(1, 1),
            Status = ExportStatus.Succeeded,
            BytesWritten = 100,
            OutputPath = "/x",
            Checksum = "abc",
            ObservedAtUtc = DateTimeOffset.UtcNow,
            AttemptNumber = 1,
        };
        await _store.RecordOutcomeAsync(o, CancellationToken.None);
        await _store.RecordOutcomeAsync(o, CancellationToken.None);

        var counters = await _store.GetCountersAsync(CancellationToken.None);
        counters.TotalRecorded.Should().Be(1);
        counters.TotalSucceeded.Should().Be(1);
        counters.TotalBytesWritten.Should().Be(100);
    }

    [Fact]
    public async Task Checkpoint_IsMonotonic()
    {
        await _store.SaveCheckpointAsync("p1", new DocumentFileVersionKey(5, 5), CancellationToken.None);
        await _store.SaveCheckpointAsync("p1", new DocumentFileVersionKey(3, 100), CancellationToken.None);
        await _store.SaveCheckpointAsync("p1", new DocumentFileVersionKey(5, 6), CancellationToken.None);

        var cp = await _store.GetCheckpointAsync("p1", CancellationToken.None);
        cp.Should().Be(new DocumentFileVersionKey(5, 6));
    }
}
