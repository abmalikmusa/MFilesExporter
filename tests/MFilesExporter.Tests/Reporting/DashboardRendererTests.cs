using FluentAssertions;
using MFilesExporter.Application.Abstractions.Dashboard;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Reporting.Dashboard;
using Spectre.Console;
using Spectre.Console.Testing;

namespace MFilesExporter.Tests.Reporting;

public class DashboardRendererTests
{
    private static DashboardSnapshot Sample() => new()
    {
        StartedAtUtc          = new DateTimeOffset(2026, 8, 4, 8, 13, 22, TimeSpan.Zero),
        ObservedAtUtc         = new DateTimeOffset(2026, 8, 4, 10, 27, 59, TimeSpan.Zero),
        TotalExpected         = 5_041_559,
        TotalProcessed        = 2_791_832,
        TotalSucceeded        = 2_791_058,
        TotalFailed           = 732,
        TotalSkipped          = 42,
        TotalBytesWritten     = 4_115_235_500_032L,   // ≈ 3.74 TiB
        TotalRetries          = 1_204,
        DocumentsPerSecond    = 481.2,
        MegabytesPerSecond    = 9.87,
        EtaSeconds            = 6_442,
        ProcessMemoryBytes    = 1_524_000_000L,
        CpuUsagePercent       = 62.3,
        DiskFreeBytes         = 904_400_000_000L,
        Workers = new[]
        {
            new WorkerActivityEntry
            {
                WorkerId = 3, State = WorkerActivityState.Busy,
                CurrentDocumentKey = "DFV#0000829174__2024_Contract_v3.pdf",
                CurrentBatchId = "batch-42",
                BytesWritten = 1_100_000L,
                LastUpdateUtc = DateTimeOffset.UtcNow,
                DocumentsProcessed = 349_012,
                DocumentsFailed = 0,
            },
        },
    };

    [Fact]
    public void Renderer_Produces_A_Layout_Containing_All_Required_Labels()
    {
        var options  = new DashboardOptions();
        var layout   = new DashboardRenderer(options).Build(Sample());

        var console = new TestConsole();
        console.Profile.Width = 160;
        console.Write(layout);
        var output = console.Output;

        output.Should().Contain("MFilesExporter");
        output.Should().Contain("Progress");
        output.Should().Contain("Throughput");
        output.Should().Contain("Current Activity");
        output.Should().Contain("Counts");
        output.Should().Contain("Resources");
        output.Should().Contain("Workers");
        output.Should().Contain("succeeded");
        output.Should().Contain("failed");
        output.Should().Contain("retries");
        output.Should().Contain("docs/s");
        output.Should().Contain("MiB/s");
        output.Should().Contain("batch-42");
    }

    [Fact]
    public void Renderer_Shows_Placeholder_When_No_Total_Expected()
    {
        var snapshot = Sample() with { TotalExpected = 0, EtaSeconds = null };
        var layout   = new DashboardRenderer(new DashboardOptions()).Build(snapshot);

        var console = new TestConsole();
        console.Profile.Width = 160;
        console.Write(layout);

        console.Output.Should().Contain("remaining");
    }

    [Fact]
    public void Renderer_Truncates_Long_Document_Keys()
    {
        var longKey = new string('X', 200);
        var snapshot = Sample() with
        {
            Workers = new[]
            {
                new WorkerActivityEntry
                {
                    WorkerId = 0,
                    State = WorkerActivityState.Busy,
                    CurrentDocumentKey = longKey,
                    LastUpdateUtc = DateTimeOffset.UtcNow,
                },
            },
        };

        var console = new TestConsole();
        console.Profile.Width = 200;
        console.Write(new DashboardRenderer(new DashboardOptions { MaxDocumentKeyLength = 20 }).Build(snapshot));

        console.Output.Should().NotContain(longKey);
        console.Output.Should().Contain("XXXXXXXXXXXXXXXXXXX…");
    }
}
