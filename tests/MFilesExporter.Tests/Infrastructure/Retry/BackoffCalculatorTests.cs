using FluentAssertions;
using MFilesExporter.Infrastructure.Retry;

namespace MFilesExporter.Tests.Infrastructure.Retry;

public class BackoffCalculatorTests
{
    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 200)]
    [InlineData(3, 400)]
    [InlineData(4, 800)]
    public void Exponential_Growth_Without_Jitter(int attempt, double expectedMs)
    {
        var delay = BackoffCalculator.Compute(
            attempt,
            baseDelay: TimeSpan.FromMilliseconds(100),
            maxDelay:  TimeSpan.FromSeconds(30),
            jitterFactor: 0.0,
            rng: new Random(1));

        delay.TotalMilliseconds.Should().Be(expectedMs);
    }

    [Fact]
    public void Delay_Is_Capped_By_MaxDelay()
    {
        var delay = BackoffCalculator.Compute(
            attemptNumber: 20,
            baseDelay: TimeSpan.FromMilliseconds(100),
            maxDelay:  TimeSpan.FromSeconds(5),
            jitterFactor: 0.0,
            rng: new Random(1));

        delay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Jitter_Stays_Within_Configured_Band()
    {
        var rng = new Random(42);
        for (int i = 0; i < 100; i++)
        {
            var delay = BackoffCalculator.Compute(
                attemptNumber: 3,
                baseDelay: TimeSpan.FromMilliseconds(100),
                maxDelay:  TimeSpan.FromSeconds(30),
                jitterFactor: 0.25,
                rng: rng);

            // Planned = 100 * 4 = 400ms → 300..500ms with ±25% jitter.
            delay.TotalMilliseconds.Should().BeInRange(300, 500);
        }
    }

    [Fact]
    public void High_Attempt_Number_Does_Not_Overflow()
    {
        var delay = BackoffCalculator.Compute(
            attemptNumber: 100_000,
            baseDelay: TimeSpan.FromMilliseconds(1),
            maxDelay:  TimeSpan.FromSeconds(10),
            jitterFactor: 0.0,
            rng: new Random(1));

        delay.Should().Be(TimeSpan.FromSeconds(10));
    }
}
