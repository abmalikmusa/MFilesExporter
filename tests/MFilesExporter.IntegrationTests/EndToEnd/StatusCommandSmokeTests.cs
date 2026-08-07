using FluentAssertions;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Console;
using MFilesExporter.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace MFilesExporter.IntegrationTests.EndToEnd;

/// <summary>
/// Proves the <c>--status</c> command can actually query the tracking
/// database. Every SQL query inside <see cref="StatusCommand"/> is inline
/// C#, so a column-name typo would ship silently — this test catches it.
/// </summary>
[Collection("SqlServer")]
public sealed class StatusCommandSmokeTests
{
    private readonly SqlServerFixture _sql;

    public StatusCommandSmokeTests(SqlServerFixture sql) => _sql = sql;

    [Fact]
    public async Task RunAgainstAsync_QueriesTrackingDb_AndReturnsExitCodeZero()
    {
        // Arrange — small run so there's at least one seeded document
        // in the vault; the tracking DB's job/worker/outcome tables may
        // still be empty because the current pipeline does not open a
        // real job (see IJobContext). That's the point of this smoke:
        // status must gracefully print "no rows" without erroring.
        await VaultSeeder.ResetAsync(_sql.SourceConnectionString).ConfigureAwait(false);
        await VaultSeeder.SeedAsync(_sql.SourceConnectionString, documentCount: 50,
            partStartId: 4_000_000L, dfvStartId: 8_000_000L).ConfigureAwait(false);

        await using var host = ExporterTestHost.Create(_sql, workerCount: 2, partitionKey: "status-smoke");
        await host.Services.GetRequiredService<IExportStateStore>()
            .InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.Pipeline.RunAsync(runCts.Token).ConfigureAwait(false);

        // Act — run the status report into a captured writer.
        var buffer = new StringWriter();
        var exitCode = await StatusCommand.RunAgainstAsync(
            _sql.TrackingConnectionString, buffer, CancellationToken.None)
            .ConfigureAwait(false);

        // Assert — exit 0, and every headline the command emits is present.
        var output = buffer.ToString();
        exitCode.Should().Be(0, "status against a reachable tracking DB should exit 0");
        output.Should().Contain("Status summary");
        output.Should().Contain("Outcomes");
        output.Should().Contain("Workers");
        output.Should().Contain("Failures by category");
        output.Should().Contain("Checkpoint");
    }

    [Fact]
    public async Task RunAgainstAsync_UnreachableDb_ReturnsExitCodeOne()
    {
        var buffer   = new StringWriter();
        var exitCode = await StatusCommand.RunAgainstAsync(
            "Server=127.0.0.1,4;Database=nope;User Id=sa;Password=x;TrustServerCertificate=True;Connection Timeout=1;",
            buffer,
            CancellationToken.None).ConfigureAwait(false);

        exitCode.Should().Be(1, "unreachable DB should produce a non-zero exit");
    }
}
