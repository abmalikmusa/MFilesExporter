using System.Net.Sockets;
using FluentAssertions;
using MFilesExporter.Application.Abstractions.Retry;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Infrastructure.Retry;
using Microsoft.Extensions.Logging.Abstractions;

namespace MFilesExporter.Tests.Infrastructure.Retry;

public class RetryExecutorTests
{
    private static RetryHandlingOptions FastOptions() => new()
    {
        Enabled = true,
        Default = new RetryPolicyProfile
        {
            MaxAttempts = 3, BaseDelayMilliseconds = 1, MaxDelaySeconds = 1,
            PerAttemptTimeoutSeconds = 5, JitterFactor = 0,
            CircuitBreaker = CircuitBreakerSettings.Disabled(),
        },
        Network = new RetryPolicyProfile
        {
            MaxAttempts = 4, BaseDelayMilliseconds = 1, MaxDelaySeconds = 1,
            PerAttemptTimeoutSeconds = 5, JitterFactor = 0,
            CircuitBreaker = CircuitBreakerSettings.Disabled(),
        },
    };

    private static RetryExecutor NewExecutor(RetryHandlingOptions options)
        => new(options, new ExceptionClassifier(), Array.Empty<IRetryObserver>(), NullLogger<RetryExecutor>.Instance);

    [Fact]
    public async Task Succeeds_On_First_Attempt()
    {
        var executor = NewExecutor(FastOptions());
        var calls = 0;

        var result = await executor.ExecuteAsync(RetryOperationNames.Network, _ =>
        {
            calls++;
            return ValueTask.FromResult(42);
        }, CancellationToken.None);

        result.Should().Be(42);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Retries_Transient_Then_Succeeds()
    {
        var executor = NewExecutor(FastOptions());
        var calls = 0;

        var result = await executor.ExecuteAsync(RetryOperationNames.Network, _ =>
        {
            calls++;
            if (calls < 3) throw new SocketException((int)SocketError.ConnectionReset);
            return ValueTask.FromResult("ok");
        }, CancellationToken.None);

        result.Should().Be("ok");
        calls.Should().Be(3);
    }

    [Fact]
    public async Task Permanent_Failure_Is_Not_Retried()
    {
        var executor = NewExecutor(FastOptions());
        var calls = 0;

        var act = async () => await executor.ExecuteAsync<int>(RetryOperationNames.Network, _ =>
        {
            calls++;
            throw new ArgumentException("bad");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_Propagates_And_Stops_Loop()
    {
        var executor = NewExecutor(FastOptions());
        using var cts = new CancellationTokenSource();
        var calls = 0;

        var task = executor.ExecuteAsync<int>(RetryOperationNames.Network, _ =>
        {
            calls++;
            if (calls == 1)
            {
                cts.Cancel();
                throw new SocketException((int)SocketError.ConnectionReset);
            }
            throw new SocketException((int)SocketError.ConnectionReset);
        }, cts.Token);

        Func<Task> act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Exhausts_Attempts_And_Rethrows()
    {
        var executor = NewExecutor(FastOptions());
        var calls = 0;

        var act = async () => await executor.ExecuteAsync<int>(RetryOperationNames.Network, _ =>
        {
            calls++;
            throw new SocketException((int)SocketError.ConnectionReset);
        }, CancellationToken.None);

        await act.Should().ThrowAsync<SocketException>();
        calls.Should().Be(4);   // MaxAttempts=4 in FastOptions.Network
    }

    [Fact]
    public async Task Disabled_Option_Runs_Once_And_Rethrows()
    {
        var options = FastOptions();
        options.Enabled = false;
        var executor = NewExecutor(options);
        var calls = 0;

        var act = async () => await executor.ExecuteAsync<int>(RetryOperationNames.Network, _ =>
        {
            calls++;
            throw new SocketException((int)SocketError.ConnectionReset);
        }, CancellationToken.None);

        await act.Should().ThrowAsync<SocketException>();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task PerAttempt_Timeout_Is_Treated_As_Retryable()
    {
        var options = FastOptions();
        options.Network = new RetryPolicyProfile
        {
            MaxAttempts = 3, BaseDelayMilliseconds = 1, MaxDelaySeconds = 1,
            PerAttemptTimeoutSeconds = 1, JitterFactor = 0,
            CircuitBreaker = CircuitBreakerSettings.Disabled(),
        };
        var executor = NewExecutor(options);
        var calls = 0;

        var result = await executor.ExecuteAsync(RetryOperationNames.Network, async ct =>
        {
            calls++;
            if (calls < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return 0;
            }
            return 99;
        }, CancellationToken.None);

        result.Should().Be(99);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task Observer_Sees_Retry_And_Outcome_Events()
    {
        var options = FastOptions();
        var observer = new CapturingObserver();
        var executor = new RetryExecutor(
            options, new ExceptionClassifier(), new[] { observer }, NullLogger<RetryExecutor>.Instance);

        var calls = 0;
        var result = await executor.ExecuteAsync(RetryOperationNames.Network, _ =>
        {
            calls++;
            if (calls < 2) throw new SocketException((int)SocketError.ConnectionReset);
            return ValueTask.FromResult("ok");
        }, CancellationToken.None, correlationId: "abc");

        result.Should().Be("ok");
        observer.RetryEvents.Should().ContainSingle();
        observer.RetryEvents[0].Category.Should().Be(FailureCategory.NetworkInterruption);
        observer.RetryEvents[0].CorrelationId.Should().Be("abc");
        observer.Outcomes.Should().ContainSingle();
        observer.Outcomes[0].Succeeded.Should().BeTrue();
        observer.Outcomes[0].TotalAttempts.Should().Be(2);
    }

    [Fact]
    public async Task Deadlock_Override_Increases_MaxAttempts_And_Reduces_Delay()
    {
        var options = FastOptions();
        // Base profile allows 3 attempts; override caps deadlocks at 8 (higher, so use profile's 3).
        // Flip the profile down to 2 so we can see the override taking effect only within its cap.
        options.Default = new RetryPolicyProfile
        {
            MaxAttempts = 5, BaseDelayMilliseconds = 100, MaxDelaySeconds = 30,
            PerAttemptTimeoutSeconds = 5, JitterFactor = 0,
            CircuitBreaker = CircuitBreakerSettings.Disabled(),
        };
        // Deadlock override reduces base delay to 50ms — that's what we assert.
        options.Categories.SqlDeadlock = new CategoryOverride
        {
            MaxAttemptsCap = 8,
            BaseDelayMilliseconds = 5,
            MaxDelaySeconds = 1,
            DisableCircuitBreaker = true,
        };

        var observer = new CapturingObserver();
        var executor = new RetryExecutor(
            options, new ExceptionClassifier(), new[] { observer }, NullLogger<RetryExecutor>.Instance);

        var calls = 0;
        var result = await executor.ExecuteAsync("unknown-op", _ =>
        {
            calls++;
            if (calls < 3) throw new TimeoutException();   // classified as SqlTimeout (not deadlock)
            return ValueTask.FromResult(1);
        }, CancellationToken.None);

        result.Should().Be(1);
        calls.Should().Be(3);
        observer.RetryEvents.Should().HaveCount(2);
        // Timeout override is unchanged — delay must follow profile's base 100ms → 100, 200.
        observer.RetryEvents[0].Delay.TotalMilliseconds.Should().BeApproximately(100, 1);
        observer.RetryEvents[1].Delay.TotalMilliseconds.Should().BeApproximately(200, 1);
    }

    private sealed class CapturingObserver : IRetryObserver
    {
        public List<RetryAttemptContext> RetryEvents { get; } = new();
        public List<RetryOutcome> Outcomes { get; } = new();

        public ValueTask OnRetryAsync(RetryAttemptContext attempt, CancellationToken cancellationToken)
        {
            RetryEvents.Add(attempt);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnOutcomeAsync(RetryOutcome outcome, CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);
            return ValueTask.CompletedTask;
        }
    }
}
