using FluentAssertions;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Export.Storage;
using MFilesExporter.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MFilesExporter.IntegrationTests.EndToEnd;

/// <summary>
/// Proves the retry classifier + circuit breaker actually recover the
/// pipeline from a transient sink fault. Uses <see cref="DiskFullFaultingSink"/>
/// to make the sink throw a DiskFull-shaped IOException on the first
/// attempt of every document.
/// </summary>
[Collection("SqlServer")]
public sealed class FaultInjectionTests
{
    private const int CorpusSize   = 50;
    private const int WorkerCount  = 4;
    private const string Partition = "fault";

    private readonly SqlServerFixture _sql;

    public FaultInjectionTests(SqlServerFixture sql) => _sql = sql;

    [Fact]
    public async Task DiskFullOnFirstAttempt_RetriesAndSucceeds()
    {
        await VaultSeeder.ResetAsync(_sql.SourceConnectionString).ConfigureAwait(false);
        var seeded = await VaultSeeder.SeedAsync(
            _sql.SourceConnectionString,
            CorpusSize,
            seed: 20260808,
            partStartId: 5_000_000L,
            dfvStartId: 9_000_000L).ConfigureAwait(false);

        // Replace the sink registration with a wrapper that fails the first
        // attempt of every document.
        DiskFullFaultingSink? faultingSink = null;
        await using var host = ExporterTestHost.Create(
            _sql,
            workerCount: WorkerCount,
            partitionKey: Partition,
            customize: services =>
            {
                services.AddSingleton<FileSystemDocumentSink>();
                services.AddSingleton<IDocumentSink>(sp =>
                {
                    faultingSink = new DiskFullFaultingSink(
                        sp.GetRequiredService<FileSystemDocumentSink>());
                    return faultingSink;
                });

                // The CB on `disk-write` trips after ~20 observed failures at
                // 50% ratio. This test injects a fault on every document, so
                // it would trip mid-run — but we're validating retry recovery,
                // not the breaker. Disable CB on this profile only.
                services.PostConfigure<ExporterOptions>(o =>
                {
                    o.RetryHandling.DiskWrite.CircuitBreaker = CircuitBreakerSettings.Disabled();
                });
            });

        await host.Services.GetRequiredService<IExportStateStore>()
            .InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await host.Pipeline.RunAsync(cts.Token).ConfigureAwait(false);

        // Assert — every document succeeded despite each throwing once.
        var counters = await host.Services.GetRequiredService<IExportStateStore>()
            .GetCountersAsync(CancellationToken.None).ConfigureAwait(false);

        counters.TotalRecorded.Should().Be(CorpusSize);
        counters.TotalSucceeded.Should().Be(CorpusSize,
            "the retry pipeline must recover from a transient DiskFull fault on every document");
        counters.TotalFailed.Should().Be(0);

        faultingSink.Should().NotBeNull();
        faultingSink!.InjectedFaultCount.Should().Be(CorpusSize,
            "the wrapper must have faulted the first attempt for every seeded document");

        // Verify the files really landed after the second attempt.
        var documentsDir = Path.Combine(host.OutputRoot, "documents");
        Directory.EnumerateFiles(documentsDir, "*", SearchOption.AllDirectories)
            .Count().Should().Be(CorpusSize);
    }

    [Fact]
    public async Task DiskFullOnEveryAttempt_WithCircuitBreakerOn_TripsAndStopsHammeringSink()
    {
        // Second angle: a *sustained* fault (every attempt throws, forever).
        // Retry alone can't recover; we expect the CB to trip and stop
        // re-attempting the sink, capping the total invocations well below
        // "CorpusSize × MaxAttempts". Proves the CB is real, not decorative.
        await VaultSeeder.ResetAsync(_sql.SourceConnectionString).ConfigureAwait(false);
        await VaultSeeder.SeedAsync(
            _sql.SourceConnectionString,
            CorpusSize,
            seed: 20260809,
            partStartId: 6_000_000L,
            dfvStartId: 10_000_000L).ConfigureAwait(false);

        AlwaysFailingSink? alwaysFail = null;
        await using var host = ExporterTestHost.Create(
            _sql,
            workerCount: 2,
            partitionKey: "fault-persistent",
            customize: services =>
            {
                services.AddSingleton<FileSystemDocumentSink>();
                services.AddSingleton<IDocumentSink>(sp =>
                {
                    alwaysFail = new AlwaysFailingSink();
                    return alwaysFail;
                });

                // Tighten CB thresholds so the trip happens quickly in a
                // 50-doc run.
                services.PostConfigure<ExporterOptions>(o =>
                {
                    o.RetryHandling.DiskWrite.CircuitBreaker.FailureRatio      = 0.5;
                    o.RetryHandling.DiskWrite.CircuitBreaker.MinimumThroughput = 4;
                });
            });

        await host.Services.GetRequiredService<IExportStateStore>()
            .InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await host.Pipeline.RunAsync(cts.Token).ConfigureAwait(false);

        var counters = await host.Services.GetRequiredService<IExportStateStore>()
            .GetCountersAsync(CancellationToken.None).ConfigureAwait(false);

        counters.TotalSucceeded.Should().Be(0,
            "every write throws — no document should succeed");
        counters.TotalFailed.Should().Be(CorpusSize,
            "every seeded document must reach a terminal Failed outcome");

        alwaysFail.Should().NotBeNull();
        // Without a breaker, we'd expect ~CorpusSize * 2 attempts (initial + 1
        // retry per DiskFull category cap). With a working CB, the sink stops
        // being hammered as soon as the breaker opens — so total invocations
        // must be strictly less than the naive upper bound.
        var naiveMaxAttempts = CorpusSize * 2;
        alwaysFail!.InvocationCount.Should().BeLessThan(naiveMaxAttempts,
            "the circuit breaker must eventually stop the executor from re-hitting a broken sink");
    }
}

/// <summary>Test double that throws DiskFull on every call — for CB-trip tests.</summary>
internal sealed class AlwaysFailingSink : IDocumentSink
{
    public int InvocationCount { get; private set; }

    public Task<DocumentSinkResult> WriteAsync(
        Domain.Documents.DocumentDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken)
    {
        InvocationCount++;
        throw new IOException("There is not enough space on the disk.");
    }
}
