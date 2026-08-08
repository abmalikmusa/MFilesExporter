using MFilesExporter.Configuration.Options;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Infrastructure.Retry;

/// <summary>
/// Sliding-window circuit breaker for a single operation.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled rather than delegated to a third-party resilience library
/// because the retry executor already owns the attempt loop — layering
/// another pipeline on top would duplicate cancellation handling and hide
/// state transitions from our observer. Raw state is exposed so the
/// executor can short-circuit and health checks can inspect the breaker.
/// </para>
/// <para>Thread-safety: all mutations happen under <c>_lock</c>. Reads are cheap.</para>
/// </remarks>
public sealed class OperationCircuitBreaker
{
    private readonly string _name;
    private readonly CircuitBreakerSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    private CircuitState _state = CircuitState.Closed;
    private int _successes;
    private int _failures;
    private DateTimeOffset _windowStart;
    private DateTimeOffset _openedAt;

    public OperationCircuitBreaker(string name, CircuitBreakerSettings settings, TimeProvider time, ILogger logger)
    {
        _name       = name;
        _settings   = settings;
        _time       = time;
        _logger     = logger;
        _windowStart = time.GetUtcNow();
    }

    public CircuitState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>Throws <see cref="CircuitOpenException"/> when the breaker is open.</summary>
    public void EnsureClosed()
    {
        if (!_settings.Enabled) return;

        lock (_lock)
        {
            if (_state == CircuitState.Open)
            {
                var elapsed = _time.GetUtcNow() - _openedAt;
                var breakDuration = TimeSpan.FromSeconds(_settings.BreakDurationSeconds);
                if (elapsed >= breakDuration)
                {
                    Transition(CircuitState.HalfOpen);
                    _logger.LogInformation("[breaker] {Operation} → HALF-OPEN", _name);
                    return;
                }

                throw new CircuitOpenException(_name, breakDuration - elapsed);
            }
        }
    }

    public void OnSuccess()
    {
        if (!_settings.Enabled) return;

        lock (_lock)
        {
            RollWindowIfNeeded();
            _successes++;

            if (_state == CircuitState.HalfOpen)
            {
                Transition(CircuitState.Closed);
                _logger.LogInformation("[breaker] {Operation} → CLOSED (probe succeeded)", _name);
            }
        }
    }

    public void OnFailure()
    {
        if (!_settings.Enabled) return;

        lock (_lock)
        {
            RollWindowIfNeeded();
            _failures++;

            if (_state == CircuitState.HalfOpen)
            {
                Trip();
                return;
            }

            var total = _successes + _failures;
            if (_state == CircuitState.Closed
                && total >= _settings.MinimumThroughput
                && (double)_failures / total >= _settings.FailureRatio)
            {
                Trip();
            }
        }
    }

    private void RollWindowIfNeeded()
    {
        var now = _time.GetUtcNow();
        var windowSize = TimeSpan.FromSeconds(_settings.SamplingDurationSeconds);
        if (now - _windowStart < windowSize) return;

        _windowStart = now;
        _successes = 0;
        _failures  = 0;
    }

    private void Trip()
    {
        Transition(CircuitState.Open);
        _openedAt = _time.GetUtcNow();
        _logger.LogWarning("[breaker] {Operation} → OPEN for {Duration}s (failures={Failures}/{Total})",
            _name, _settings.BreakDurationSeconds, _failures, _successes + _failures);
    }

    private void Transition(CircuitState newState)
    {
        _state = newState;
        _successes = 0;
        _failures  = 0;
        _windowStart = _time.GetUtcNow();
    }
}

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}
