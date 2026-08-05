# Enterprise Retry Handling

> _Layer: `MFilesExporter.Application.Abstractions.Retry` (contracts) + `MFilesExporter.Infrastructure.Retry` (implementation)_
> _Namespace: `MFilesExporter.Infrastructure.Retry`_

## 1. Purpose

Every I/O boundary in the exporter can fail: SQL Server timeouts, deadlocks,
transient throttling, TCP resets, disk-full errors, and permission denials
happen daily at 5-million-document scale. This module centralises how those
failures are classified, retried, and observed so no caller re-implements the
policy.

The retry engine sits **above** every persistence, sink, or network adapter
and **below** the business use case. It is invoked through a single interface
— `IRetryExecutor` — and every observable behaviour is driven from configuration
under `Exporter:RetryHandling`.

## 2. Failure Taxonomy

`FailureCategory` is the canonical classification. Every exception is mapped
to exactly one category, which decides retry eligibility, back-off, and
whether the operation is subject to a circuit breaker.

| Category               | Retryable | Back-off | Circuit breaker | Example sources |
|------------------------|-----------|----------|-----------------|-----------------|
| `SqlTimeout`           | yes       | expo     | yes             | SQL error `-2`, `TimeoutException`, `SocketError.TimedOut` |
| `SqlDeadlock`          | yes       | short    | disabled by override | SQL `1205`, `1222` |
| `SqlTransient`         | yes       | expo     | yes             | SQL `4060`, `4221`, `615` |
| `NetworkInterruption`  | yes       | expo     | yes             | SQL `10053/54/60/61`, `SocketException(ConnectionReset)` |
| `IoFailure`            | yes       | expo     | yes             | `IOException`, sharing violation |
| `DiskFull`             | limited   | slow     | overridden      | HResult `0x80070070`, `errno ENOSPC`, Win32 `112` |
| `PermissionDenied`     | no        | –        | –               | `UnauthorizedAccessException`, SQL `18456` |
| `RateLimited`          | yes       | slow     | yes             | SQL `40501`, `40613`, `49918-20`, HTTP 429 |
| `Cancelled`            | no        | –        | –               | `OperationCanceledException` (linked to caller CT) |
| `Permanent`            | no        | –        | –               | `ArgumentException`, `InvalidOperationException` |
| `Unknown`              | no        | –        | –               | anything not matched — treated as permanent |

Rules of thumb encoded in the classifier:

- **Deadlocks retry fast, without CB.** A deadlock is self-healing; ratcheting
  the CB open on the second one would kill throughput.
- **Disk-full retries are polite and few.** One or two attempts with a 1 s
  delay in case another process frees space; beyond that the operator has a
  real problem.
- **Cancellation always wins.** If the caller's token fires we never retry —
  that would defeat cooperative cancellation.
- **`Unknown` = permanent.** Better to surface bugs immediately than sink them
  into a retry loop.

## 3. Contract

```csharp
public interface IRetryExecutor
{
    ValueTask<T> ExecuteAsync<T>(
        string operationName,
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken,
        string? correlationId = null);

    ValueTask ExecuteAsync(
        string operationName,
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken,
        string? correlationId = null);
}
```

`operationName` selects a `RetryPolicyProfile` and identifies the circuit
breaker instance. Canonical names live in `RetryOperationNames`:

- `sql-read` — enumeration, small SELECTs
- `sql-blob-read` — streaming BLOB pulls (longer timeouts)
- `sql-write` — tracking-DB writes
- `disk-write` — file sink writes
- `disk-read` — local file reads (checksum verify, WAL replay)
- `state-store` — SQLite / state-store calls
- `network` — generic HTTP or arbitrary network I/O

Unknown names fall through to the `Default` profile.

## 4. Configuration

Everything is bound from `Exporter:RetryHandling`:

```jsonc
"Exporter": {
  "RetryHandling": {
    "Enabled": true,

    "Default": {
      "MaxAttempts": 3,
      "BaseDelayMilliseconds": 250,
      "MaxDelaySeconds": 15,
      "PerAttemptTimeoutSeconds": 60,
      "JitterFactor": 0.25,
      "CircuitBreaker": {
        "Enabled": true,
        "FailureRatio": 0.5,
        "MinimumThroughput": 20,
        "SamplingDurationSeconds": 30,
        "BreakDurationSeconds": 30
      }
    },

    "SqlRead":     { "MaxAttempts": 5, "BaseDelayMilliseconds": 500, "MaxDelaySeconds": 30, "PerAttemptTimeoutSeconds": 300 },
    "SqlBlobRead": { "MaxAttempts": 5, "BaseDelayMilliseconds": 500, "MaxDelaySeconds": 30, "PerAttemptTimeoutSeconds": 600 },
    "SqlWrite":    { "MaxAttempts": 5, "BaseDelayMilliseconds": 250, "MaxDelaySeconds": 20, "PerAttemptTimeoutSeconds": 120 },
    "DiskWrite":   { "MaxAttempts": 3, "BaseDelayMilliseconds": 250, "MaxDelaySeconds": 15, "PerAttemptTimeoutSeconds": 300 },
    "DiskRead":    { "MaxAttempts": 3, "BaseDelayMilliseconds": 100, "MaxDelaySeconds":  5, "PerAttemptTimeoutSeconds":  60 },
    "StateStore":  { "MaxAttempts": 5, "BaseDelayMilliseconds": 100, "MaxDelaySeconds":  5, "PerAttemptTimeoutSeconds":  30 },
    "Network":     { "MaxAttempts": 5, "BaseDelayMilliseconds": 500, "MaxDelaySeconds": 30, "PerAttemptTimeoutSeconds":  60 },

    "Categories": {
      "SqlDeadlock": { "MaxAttemptsCap": 8, "BaseDelayMilliseconds": 50, "MaxDelaySeconds": 2, "DisableCircuitBreaker": true },
      "DiskFull":    { "MaxAttemptsCap": 2, "BaseDelayMilliseconds": 1000, "MaxDelaySeconds": 5 },
      "RateLimited": { "BaseDelayMilliseconds": 1000, "MaxDelaySeconds": 60 }
    }
  }
}
```

**Category overrides** apply *after* profile lookup — they never expand an
operation's `MaxAttempts` beyond its cap, but they let you tune the shape
of retries for a specific failure mode without duplicating profiles.

## 5. Back-off algorithm

`BackoffCalculator.Compute` is a pure function extracted for unit testability.

```
planned  = min(Base · 2^(attempt-1), MaxDelay)
delay    = uniform(planned · (1 - Jitter), planned · (1 + Jitter))
```

- Full jitter (default `J = 0.25`) prevents synchronised herd retries when
  many workers hit the same throttle at the same instant.
- The exponent is clamped at 30 so pathological attempt counts do not shift
  into negative or NaN durations.
- Set `JitterFactor: 0.0` to disable jitter — useful for deterministic tests
  but never in production.

## 6. Circuit breaker

Each operation has its own `OperationCircuitBreaker`. The breaker uses a
rolling-window failure ratio:

- **Closed** — calls flow through; failures and successes are counted in the
  current window.
- **Open** — every call is short-circuited with `CircuitOpenException` until
  `BreakDurationSeconds` elapses.
- **Half-open** — the next call is a probe. Success → back to Closed;
  failure → straight back to Open.

The breaker is *layered under* the retry loop: a single failure trips
`OnFailure()`, but the executor still applies its own back-off and retries
inside the same call. This means a breaker trip does not immediately
propagate to the caller unless the retry budget is also exhausted.

Categories that override `DisableCircuitBreaker` (e.g. `SqlDeadlock`) bypass
both counting and short-circuit logic.

## 7. Per-attempt timeout

Each attempt runs under a linked `CancellationTokenSource` with
`PerAttemptTimeoutSeconds`. If the token fires:

- when the outer caller cancelled → propagates `OperationCanceledException`,
- when the timeout fired → materialised as a `TimeoutException` and
  reclassified as `SqlTimeout` → retried like any other transient failure.

This yields the enterprise behaviour where a slow individual call no longer
poisons the whole operation.

## 8. Observability

Every attempt fires two hooks:

- `IRetryObserver.OnRetryAsync` — called before the executor sleeps.
- `IRetryObserver.OnOutcomeAsync` — called exactly once per top-level call.

Bundled implementations:

| Observer                | Purpose |
|-------------------------|---------|
| `LoggingRetryObserver`  | Structured logs at INFO/ERROR with `operation`, `attempt`, `category`, `delay`, `correlationId`. |
| `MetricsRetryObserver`  | OpenTelemetry counters `exporter.retry.attempts`, `exporter.retry.outcomes`, and histogram `exporter.retry.elapsed_ms`. Meter name: `MFilesExporter.Retry`. |

Observer exceptions are caught and logged — they never poison the caller.

## 9. Sequence — successful retry

```
caller ──▶ RetryExecutor.ExecuteAsync("sql-read", op, ct)
             │
             ├── breaker.EnsureClosed()               ✔
             ├── linkedCts (per-attempt timeout)
             ├── attempt 1 ─────────────▶ op ▷ SqlException(1205)
             ├── classifier ─▶ SqlDeadlock
             ├── override: max=8, base=50ms, breaker off
             ├── sleep 50 ± 12ms (jitter)
             ├── attempt 2 ─────────────▶ op ▷ success
             ├── breaker.OnSuccess()
             └── observers.OnOutcome(succeeded=true, attempts=2)
```

## 10. Sequence — circuit trip and short-circuit

```
worker A ─▶ ExecuteAsync("sql-read", …) ─▶ 20× SqlException(-2 timeout)
                                             │
                                             ▼
                                       breaker → OPEN
worker B ─▶ ExecuteAsync("sql-read", …)
             │
             └── breaker.EnsureClosed() ▷ throws CircuitOpenException(30 s)
             (caller can treat as transient; higher layer decides)

...30 s later...
worker C ─▶ ExecuteAsync("sql-read", …)
             │
             ├── breaker → HALF-OPEN
             ├── probe attempt ▷ success
             └── breaker → CLOSED
```

## 11. Wiring

Registered by `InfrastructureServiceCollectionExtensions.AddExporterInfrastructure`:

```csharp
services.AddSingleton<IFailureClassifier, ExceptionClassifier>();
services.AddSingleton<IRetryObserver, LoggingRetryObserver>();
services.AddSingleton<IRetryObserver, MetricsRetryObserver>();
services.AddSingleton<IRetryExecutor, RetryExecutor>();
services.AddSingleton(TimeProvider.System);
```

The `TimeProvider` singleton is what tests swap for a `FakeTimeProvider` to
drive circuit-breaker transitions without wall-clock waits.

## 12. Usage

```csharp
public sealed class DocumentRepository
{
    private readonly IRetryExecutor _retry;
    private readonly SqlConnectionFactory _factory;

    public ValueTask<DocumentBlob> ReadBlobAsync(DocumentId id, CancellationToken ct)
        => _retry.ExecuteAsync(
            RetryOperationNames.SqlBlobRead,
            async token =>
            {
                using var conn = await _factory.OpenAsync(token);
                return await ReadBlobCore(conn, id, token);
            },
            ct,
            correlationId: id.ToString());
}
```

Callers **must not** wrap their own retry loop over `IRetryExecutor` — the
executor already owns retries and observability. If you need bespoke retry
semantics, add a new profile to `RetryHandlingOptions` and reuse the executor.

## 13. Testing coverage

| Test file | Focus |
|-----------|-------|
| `BackoffCalculatorTests` | Exponent growth, max-delay cap, jitter band, overflow guard |
| `ExceptionClassifierTests` | SQL error numbers, socket errors, IO/Win32 codes, aggregate unwrap |
| `OperationCircuitBreakerTests` | Closed → Open → Half-Open transitions with a fake `TimeProvider` |
| `RetryExecutorTests` | Success, retry-then-success, permanent-not-retried, exhausted, per-attempt timeout, observers, cancellation |

## 14. What this module does NOT do

- **No side-effect deduplication.** If your operation is non-idempotent, wrap
  it with the checkpoint / fencing-token engines *before* handing to retry.
- **No dead-letter routing.** Permanent failures rethrow; the caller records
  them via the tracking DB / work-claim engine.
- **No back-pressure to the caller.** When `CircuitOpenException` throws the
  higher-level pipeline decides whether to pause producers.
- **No cross-process circuit sharing.** Each process has its own breaker
  state — deliberately, so a slow node does not black-hole the fleet.
