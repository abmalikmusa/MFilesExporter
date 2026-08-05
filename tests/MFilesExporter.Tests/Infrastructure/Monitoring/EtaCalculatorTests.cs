using FluentAssertions;
using MFilesExporter.Infrastructure.Monitoring;

namespace MFilesExporter.Tests.Infrastructure.Monitoring;

public class EtaCalculatorTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Returns_Null_When_Not_Started()
    {
        EtaCalculator.EstimateSeconds(0, 100, null, Origin).Should().BeNull();
    }

    [Fact]
    public void Returns_Null_When_No_Expected_Total()
    {
        EtaCalculator.EstimateSeconds(10, 0, Origin, Origin.AddSeconds(1)).Should().BeNull();
    }

    [Fact]
    public void Returns_Null_When_No_Progress_Yet()
    {
        EtaCalculator.EstimateSeconds(0, 100, Origin, Origin.AddSeconds(1)).Should().BeNull();
    }

    [Fact]
    public void Returns_Zero_When_Already_Complete()
    {
        EtaCalculator.EstimateSeconds(100, 100, Origin, Origin.AddSeconds(1)).Should().Be(0);
        EtaCalculator.EstimateSeconds(150, 100, Origin, Origin.AddSeconds(1)).Should().Be(0);
    }

    [Fact]
    public void Projects_Remaining_Time_From_Linear_Rate()
    {
        // 100 documents in 10 seconds → 10 docs/s. Remaining 900 → 90 s.
        EtaCalculator.EstimateSeconds(100, 1000, Origin, Origin.AddSeconds(10)).Should().Be(90);
    }

    [Fact]
    public void Handles_Very_High_Rate()
    {
        EtaCalculator.EstimateSeconds(1_000_000, 5_000_000, Origin, Origin.AddSeconds(60))
            .Should().BeApproximately(240, 0.01);
    }
}
