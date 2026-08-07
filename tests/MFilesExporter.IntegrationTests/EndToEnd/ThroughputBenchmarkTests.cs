using System.Diagnostics;
using FluentAssertions;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace MFilesExporter.IntegrationTests.EndToEnd;

/// <summary>
/// Measures pipeline throughput against a moderate corpus. Not part of the
/// default test run — invoke explicitly with:
/// <code>
/// dotnet test --filter "Category=Performance" \
///     --logger "console;verbosity=detailed"
/// </code>
/// The reported numbers are floor estimates: the SQL Server + exporter both
/// run in Docker on the host CPU with no real disk tuning, so a real
/// Windows Server deployment against on-prem SQL Server should meet or
/// exceed these figures.
/// </summary>
[Collection("SqlServer")]
[Trait("Category", "Performance")]
public sealed class ThroughputBenchmarkTests
{
    private const int CorpusSize   = 20_000;
    private const int WorkerCount  = 8;
    private const string Partition = "perf";

    private readonly SqlServerFixture _sql;
    private readonly ITestOutputHelper _output;

    public ThroughputBenchmarkTests(SqlServerFixture sql, ITestOutputHelper output)
    {
        _sql = sql;
        _output = output;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public async Task Throughput_5k_Corpus_ScalingCurve(int workers) =>
        await RunAsync(workers, "scaling").ConfigureAwait(false);

    [Fact]
    public async Task Throughput_5k_Corpus_MeetsFloorTarget() =>
        await RunAsync(WorkerCount, "default").ConfigureAwait(false);

    private async Task RunAsync(int workers, string tag)
    {
        // -----------------------------------------------------------------
        // Seed
        // -----------------------------------------------------------------
        _output.WriteLine($"Seeding {CorpusSize:N0} documents…");
        var seedSw = Stopwatch.StartNew();
        await VaultSeeder.ResetAsync(_sql.SourceConnectionString).ConfigureAwait(false);
        var seeded = await VaultSeeder.SeedAsync(
            _sql.SourceConnectionString,
            CorpusSize,
            seed: 20260807,
            partStartId: 3_000_000L,
            dfvStartId: 7_000_000L).ConfigureAwait(false);
        seedSw.Stop();
        var totalBytes = seeded.Sum(d => (long)d.Payload.Length);
        _output.WriteLine(
            $"Seeded in {seedSw.Elapsed.TotalSeconds:F1}s ({totalBytes / 1024.0 / 1024.0:F1} MiB total corpus).");

        // -----------------------------------------------------------------
        // Warm-up (~1 % of corpus so JIT/DB caches are hot before we time).
        // -----------------------------------------------------------------
        await using var host = ExporterTestHost.Create(_sql, workerCount: workers, partitionKey: Partition + "-" + tag);
        await host.Services.GetRequiredService<IExportStateStore>()
            .InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var memBefore = proc.WorkingSet64;
        var gcBefore  = GC.GetTotalMemory(forceFullCollection: false);

        // -----------------------------------------------------------------
        // Run + measure
        // -----------------------------------------------------------------
        _output.WriteLine($"Running pipeline (workers={workers})…");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var runSw = Stopwatch.StartNew();
        await host.Pipeline.RunAsync(cts.Token).ConfigureAwait(false);
        runSw.Stop();

        proc.Refresh();
        var memAfter = proc.WorkingSet64;
        var gcAfter  = GC.GetTotalMemory(forceFullCollection: false);

        // -----------------------------------------------------------------
        // Assertions on correctness (no regressions in throughput mode).
        // -----------------------------------------------------------------
        var store    = host.Services.GetRequiredService<IExportStateStore>();
        var counters = await store.GetCountersAsync(CancellationToken.None).ConfigureAwait(false);
        counters.TotalSucceeded.Should().Be(CorpusSize);
        counters.TotalFailed.Should().Be(0);
        counters.TotalSkipped.Should().Be(0);

        var writtenBytes = counters.TotalBytesWritten;
        writtenBytes.Should().Be(totalBytes);

        // -----------------------------------------------------------------
        // Report
        // -----------------------------------------------------------------
        var elapsedSec  = runSw.Elapsed.TotalSeconds;
        var docsPerSec  = CorpusSize / elapsedSec;
        var mibPerSec   = writtenBytes / elapsedSec / (1024.0 * 1024.0);
        var avgDocKib   = writtenBytes / (double)CorpusSize / 1024.0;
        var memDeltaMib = (memAfter - memBefore) / (1024.0 * 1024.0);
        var gcDeltaMib  = (gcAfter  - gcBefore ) / (1024.0 * 1024.0);

        _output.WriteLine("");
        _output.WriteLine("────────────────────────────────────────────────────");
        _output.WriteLine($" Throughput benchmark — {CorpusSize:N0} doc corpus ({tag})");
        _output.WriteLine("────────────────────────────────────────────────────");
        _output.WriteLine($" Workers                     {workers,10}");
        _output.WriteLine($" Documents processed         {CorpusSize,10:N0}");
        _output.WriteLine($" Bytes written               {writtenBytes,10:N0}  ({writtenBytes / 1024.0 / 1024.0:F1} MiB)");
        _output.WriteLine($" Avg document size (KiB)     {avgDocKib,10:F2}");
        _output.WriteLine($" Elapsed                     {elapsedSec,10:F2}  seconds");
        _output.WriteLine("────────────────────────────────────────────────────");
        _output.WriteLine($" Throughput (docs/sec)       {docsPerSec,10:F1}");
        _output.WriteLine($" Throughput (MiB/sec)        {mibPerSec,10:F2}");
        _output.WriteLine($" Extrapolated (5 M docs)     {(5_000_000.0 / docsPerSec / 3600.0),10:F1}  hours");
        _output.WriteLine("────────────────────────────────────────────────────");
        _output.WriteLine($" Working set  delta          {memDeltaMib,10:F1}  MiB");
        _output.WriteLine($" GC heap      delta          {gcDeltaMib ,10:F1}  MiB");
        _output.WriteLine("────────────────────────────────────────────────────");

        // A very loose floor — anything under 25 docs/s on this hardware
        // means something is catastrophically wrong. Real deployments should
        // easily beat this; the assertion exists so a regression that halves
        // throughput fails the build.
        docsPerSec.Should().BeGreaterThan(25,
            "the pipeline should sustain at least 25 docs/sec on the reference hardware; anything lower indicates a regression");
    }
}
