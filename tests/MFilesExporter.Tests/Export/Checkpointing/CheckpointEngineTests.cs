using FluentAssertions;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Models.Tracking;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Export.Checkpointing;
using MFilesExporter.Export.Checkpointing.WriteAheadLog;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MFilesExporter.Tests.Export.Checkpointing;

public class CheckpointEngineTests : IDisposable
{
    private readonly string _dir;

    public CheckpointEngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mfx-ckpt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private CheckpointOptions Opts() => new()
    {
        WalDirectory = _dir,
        FsyncOnWrite = false,           // faster tests
        PersistToTrackingDb = true,
        SqlSaveTimeout = TimeSpan.FromSeconds(2),
        ReconcileSqlOnRecovery = true,
    };

    private static IClock FrozenClock(DateTimeOffset now)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(now);
        return clock;
    }

    private static ExportCheckpointRecord SqlRecord(long part, long ver, long docs, DateTime at) =>
        new(1, 1, "p", part, ver, docs, at, ExportCheckpointStatus.Active);

    /* ----------------------- Recovery ----------------------- */

    [Fact]
    public async Task Recover_ReturnsOrigin_WhenNothingPersisted()
    {
        var opts = Opts();
        var repo = Substitute.For<IExportCheckpointRepository>();
        repo.GetActiveAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ExportCheckpointRecord?)null);

        var wal = new FileCheckpointWal(opts, NullLogger<FileCheckpointWal>.Instance);
        var engine = new CheckpointEngine(wal, repo, FrozenClock(DateTimeOffset.UtcNow), opts,
            NullLogger<CheckpointEngine>.Instance);

        var state = await engine.RecoverAsync(1, "p", default);

        state.Cursor.Should().Be(DocumentFileVersionKey.Origin);
        state.Source.Should().Be(CheckpointSource.Origin);
    }

    [Fact]
    public async Task Recover_PrefersHigher_WhenWalAheadOfSql()
    {
        var opts = Opts();
        var wal = new FileCheckpointWal(opts, NullLogger<FileCheckpointWal>.Instance);
        // WAL has (10, 20); SQL has (5, 5)
        await wal.AppendAsync(1, "p", new WalEntry(new DocumentFileVersionKey(10, 20), 500, DateTimeOffset.UtcNow), default);

        var repo = Substitute.For<IExportCheckpointRepository>();
        repo.GetActiveAsync(1, "p", Arg.Any<CancellationToken>())
            .Returns(SqlRecord(5, 5, 200, DateTime.UtcNow.AddMinutes(-5)));

        var engine = new CheckpointEngine(wal, repo, FrozenClock(DateTimeOffset.UtcNow), opts,
            NullLogger<CheckpointEngine>.Instance);

        var state = await engine.RecoverAsync(1, "p", default);

        state.Cursor.Should().Be(new DocumentFileVersionKey(10, 20));
        state.DocumentsProcessedInPartition.Should().Be(500);
        state.Source.Should().Be(CheckpointSource.Wal);

        // Reconciliation: SQL should have been back-filled with the WAL cursor.
        await repo.Received().SaveAsync(1, "p", 10, 20, 500, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Recover_PrefersHigher_WhenSqlAheadOfWal()
    {
        var opts = Opts();
        var wal = new FileCheckpointWal(opts, NullLogger<FileCheckpointWal>.Instance);
        await wal.AppendAsync(1, "p", new WalEntry(new DocumentFileVersionKey(3, 3), 100, DateTimeOffset.UtcNow.AddMinutes(-10)), default);

        var repo = Substitute.For<IExportCheckpointRepository>();
        repo.GetActiveAsync(1, "p", Arg.Any<CancellationToken>())
            .Returns(SqlRecord(50, 60, 3000, DateTime.UtcNow));

        var engine = new CheckpointEngine(wal, repo, FrozenClock(DateTimeOffset.UtcNow), opts,
            NullLogger<CheckpointEngine>.Instance);

        var state = await engine.RecoverAsync(1, "p", default);

        state.Cursor.Should().Be(new DocumentFileVersionKey(50, 60));
        state.DocumentsProcessedInPartition.Should().Be(3000);
        state.Source.Should().Be(CheckpointSource.SqlServer);

        // WAL back-filled to the SQL value.
        var walBack = await wal.ReadLatestAsync(1, "p", default);
        walBack!.Cursor.Should().Be(new DocumentFileVersionKey(50, 60));
    }

    [Fact]
    public async Task Recover_ReportsAgreement_WhenWalEqualsSql()
    {
        var opts = Opts();
        var cursor = new DocumentFileVersionKey(7, 7);
        var wal = new FileCheckpointWal(opts, NullLogger<FileCheckpointWal>.Instance);
        await wal.AppendAsync(1, "p", new WalEntry(cursor, 999, DateTimeOffset.UtcNow), default);

        var repo = Substitute.For<IExportCheckpointRepository>();
        repo.GetActiveAsync(1, "p", Arg.Any<CancellationToken>())
            .Returns(SqlRecord(7, 7, 999, DateTime.UtcNow));

        var engine = new CheckpointEngine(wal, repo, FrozenClock(DateTimeOffset.UtcNow), opts,
            NullLogger<CheckpointEngine>.Instance);

        var state = await engine.RecoverAsync(1, "p", default);
        state.Source.Should().Be(CheckpointSource.WalAndSql);
        state.Cursor.Should().Be(cursor);
    }

    /* ----------------------- Save ----------------------- */

    [Fact]
    public async Task Save_WritesWalAndSql_AndReturnsAdvanced()
    {
        var opts = Opts();
        var wal = new FileCheckpointWal(opts, NullLogger<FileCheckpointWal>.Instance);
        var repo = Substitute.For<IExportCheckpointRepository>();
        repo.SaveAsync(1, "p", 10L, 20L, 500L, Arg.Any<CancellationToken>()).Returns(true);

        var engine = new CheckpointEngine(wal, repo, FrozenClock(DateTimeOffset.UtcNow), opts,
            NullLogger<CheckpointEngine>.Instance);

        var result = await engine.SaveAsync(1, "p",
            new CheckpointCandidate(new DocumentFileVersionKey(10, 20), 500),
            default);

        result.WalWritten.Should().BeTrue();
        result.SqlWritten.Should().BeTrue();
        result.Advanced.Should().BeTrue();

        // WAL now has the value.
        var walEntry = await wal.ReadLatestAsync(1, "p", default);
        walEntry!.Cursor.Should().Be(new DocumentFileVersionKey(10, 20));
    }

    [Fact]
    public async Task Save_SucceedsWalOnly_WhenSqlThrows()
    {
        var opts = Opts();
        var wal = new FileCheckpointWal(opts, NullLogger<FileCheckpointWal>.Instance);
        var repo = Substitute.For<IExportCheckpointRepository>();
        repo.SaveAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("SQL down"));

        var engine = new CheckpointEngine(wal, repo, FrozenClock(DateTimeOffset.UtcNow), opts,
            NullLogger<CheckpointEngine>.Instance);

        var result = await engine.SaveAsync(1, "p",
            new CheckpointCandidate(new DocumentFileVersionKey(1, 1), 10),
            default);

        result.WalWritten.Should().BeTrue();
        result.SqlWritten.Should().BeFalse();
        result.Advanced.Should().BeTrue(); // WAL side still authoritative
        result.Warning.Should().NotBeNull();
    }

    [Fact]
    public async Task Save_TrackingDbDisabled_SkipsSql()
    {
        var opts = Opts(); opts.PersistToTrackingDb = false;
        var wal = new FileCheckpointWal(opts, NullLogger<FileCheckpointWal>.Instance);
        var repo = Substitute.For<IExportCheckpointRepository>();

        var engine = new CheckpointEngine(wal, repo, FrozenClock(DateTimeOffset.UtcNow), opts,
            NullLogger<CheckpointEngine>.Instance);

        var result = await engine.SaveAsync(1, "p",
            new CheckpointCandidate(new DocumentFileVersionKey(1, 1), 10),
            default);

        result.WalWritten.Should().BeTrue();
        result.SqlWritten.Should().BeFalse();
        await repo.DidNotReceive().SaveAsync(
            Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(),
            Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_ThenRecover_YieldsSameCursor()
    {
        var opts = Opts();
        var wal = new FileCheckpointWal(opts, NullLogger<FileCheckpointWal>.Instance);
        var repo = Substitute.For<IExportCheckpointRepository>();
        repo.SaveAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(true);
        repo.GetActiveAsync(1, "p", Arg.Any<CancellationToken>()).Returns((ExportCheckpointRecord?)null);

        var engine = new CheckpointEngine(wal, repo, FrozenClock(DateTimeOffset.UtcNow), opts,
            NullLogger<CheckpointEngine>.Instance);

        await engine.SaveAsync(1, "p", new CheckpointCandidate(new DocumentFileVersionKey(42, 43), 999), default);
        var state = await engine.RecoverAsync(1, "p", default);

        state.Cursor.Should().Be(new DocumentFileVersionKey(42, 43));
        state.DocumentsProcessedInPartition.Should().Be(999);
    }
}
