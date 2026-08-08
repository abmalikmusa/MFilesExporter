using FluentAssertions;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.IntegrationTests.Fixtures;
using MFilesExporter.Persistence.MFiles;
using Microsoft.Extensions.DependencyInjection;

namespace MFilesExporter.IntegrationTests.EndToEnd;

/// <summary>
/// Source-side counterpart to <see cref="FaultInjectionTests"/>. The existing
/// suite covers sink failures; this one proves the retry engine recovers
/// when the vault (<see cref="ISqlConnectionFactory.OpenAsync"/>) throws a
/// transient fault mid-run — which happens in practice on flaky VPN links,
/// SQL Server failovers, and pool exhaustion.
/// </summary>
[Collection("SqlServer")]
public sealed class ProducerFaultInjectionTests
{
    private const int CorpusSize   = 40;
    private const int WorkerCount  = 2;
    private const string Partition = "producer-fault";

    private readonly SqlServerFixture _sql;

    public ProducerFaultInjectionTests(SqlServerFixture sql) => _sql = sql;

    [Fact]
    public async Task SqlOpenTransientFault_IsAbsorbedByRetryExecutor_AllDocumentsSucceed()
    {
        await VaultSeeder.ResetAsync(_sql.SourceConnectionString).ConfigureAwait(false);
        await VaultSeeder.SeedAsync(
            _sql.SourceConnectionString,
            CorpusSize,
            seed: 20260811,
            partStartId: 11_000_000L,
            dfvStartId:  15_000_000L).ConfigureAwait(false);

        // The vault-facing SqlRead profile grants MaxAttempts=5. Injecting
        // 2 faults gives us room to fail *and* observe recovery in the same
        // run — one attempt is trivial, we want a repeatable retry pattern.
        const int faultsToInject = 2;

        FirstNOpensFaultingConnectionFactory? faultingFactory = null;
        await using var host = ExporterTestHost.Create(
            _sql,
            workerCount: WorkerCount,
            partitionKey: Partition,
            customize: services =>
            {
                // Register the concrete internal factory under itself, then
                // replace the ISqlConnectionFactory registration with our
                // fault-injecting wrapper that decorates it. Last one wins.
                services.AddSingleton<SqlConnectionFactory>();
                services.AddSingleton<ISqlConnectionFactory>(sp =>
                {
                    faultingFactory = new FirstNOpensFaultingConnectionFactory(
                        sp.GetRequiredService<SqlConnectionFactory>(),
                        faultsToInject,
                        () => new TimeoutException("Source vault connection open timed out (injected)."));
                    return faultingFactory;
                });
            });

        await host.Services.GetRequiredService<IExportStateStore>()
            .InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await host.Pipeline.RunAsync(cts.Token).ConfigureAwait(false);

        // -----------------------------------------------------------------
        // Assert — the pipeline finished cleanly and the retry engine ate
        // the transient faults.
        // -----------------------------------------------------------------
        var counters = await host.Services.GetRequiredService<IExportStateStore>()
            .GetCountersAsync(CancellationToken.None).ConfigureAwait(false);

        counters.TotalRecorded.Should().Be(CorpusSize);
        counters.TotalSucceeded.Should().Be(CorpusSize,
            "the retry executor must absorb transient source-side SQL faults so the pipeline still completes");
        counters.TotalFailed.Should().Be(0);

        faultingFactory.Should().NotBeNull();
        faultingFactory!.InjectedFaultCount.Should().Be(faultsToInject,
            "the wrapper must have actually thrown its configured number of faults");

        // Files on disk match the seeded corpus — the whole pipeline flowed
        // end-to-end past the injected faults.
        Directory.EnumerateFiles(Path.Combine(host.OutputRoot, "documents"), "*", SearchOption.AllDirectories)
            .Count().Should().Be(CorpusSize);
    }
}
