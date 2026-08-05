using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Infrastructure.Retry;
using Microsoft.Extensions.Logging.Abstractions;

namespace MFilesExporter.Tests.Infrastructure.Retry;

public class OperationCircuitBreakerTests
{
    private static CircuitBreakerSettings Sensitive() => new()
    {
        Enabled = true,
        FailureRatio = 0.5,
        MinimumThroughput = 4,
        SamplingDurationSeconds = 60,
        BreakDurationSeconds = 1,
    };

    [Fact]
    public void Starts_Closed()
    {
        var breaker = new OperationCircuitBreaker("op", Sensitive(), TimeProvider.System, NullLogger.Instance);
        breaker.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void Trips_After_Failure_Ratio_Exceeded()
    {
        var fake = new FakeTimeProvider();
        var breaker = new OperationCircuitBreaker("op", Sensitive(), fake, NullLogger.Instance);

        // Under minimum throughput — no trip.
        breaker.OnFailure(); breaker.OnFailure();
        breaker.State.Should().Be(CircuitState.Closed);

        // Reach threshold with all failures.
        breaker.OnFailure(); breaker.OnFailure();
        breaker.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public void EnsureClosed_Throws_When_Open()
    {
        var fake = new FakeTimeProvider();
        var breaker = new OperationCircuitBreaker("op", Sensitive(), fake, NullLogger.Instance);
        for (int i = 0; i < 4; i++) breaker.OnFailure();

        var act = breaker.EnsureClosed;
        act.Should().Throw<CircuitOpenException>();
    }

    [Fact]
    public void Transitions_To_HalfOpen_After_Break_Duration()
    {
        var fake = new FakeTimeProvider();
        var breaker = new OperationCircuitBreaker("op", Sensitive(), fake, NullLogger.Instance);
        for (int i = 0; i < 4; i++) breaker.OnFailure();
        breaker.State.Should().Be(CircuitState.Open);

        fake.Advance(TimeSpan.FromSeconds(2));
        breaker.EnsureClosed();   // triggers transition
        breaker.State.Should().Be(CircuitState.HalfOpen);
    }

    [Fact]
    public void Successful_Probe_Closes_Circuit()
    {
        var fake = new FakeTimeProvider();
        var breaker = new OperationCircuitBreaker("op", Sensitive(), fake, NullLogger.Instance);
        for (int i = 0; i < 4; i++) breaker.OnFailure();
        fake.Advance(TimeSpan.FromSeconds(2));
        breaker.EnsureClosed();   // HALF-OPEN

        breaker.OnSuccess();
        breaker.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void Failed_Probe_Reopens_Circuit()
    {
        var fake = new FakeTimeProvider();
        var breaker = new OperationCircuitBreaker("op", Sensitive(), fake, NullLogger.Instance);
        for (int i = 0; i < 4; i++) breaker.OnFailure();
        fake.Advance(TimeSpan.FromSeconds(2));
        breaker.EnsureClosed();   // HALF-OPEN

        breaker.OnFailure();
        breaker.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public void Disabled_Breaker_Never_Trips()
    {
        var breaker = new OperationCircuitBreaker("op", CircuitBreakerSettings.Disabled(), TimeProvider.System, NullLogger.Instance);
        for (int i = 0; i < 100; i++) breaker.OnFailure();
        breaker.State.Should().Be(CircuitState.Closed);
        breaker.EnsureClosed();   // no throw
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
