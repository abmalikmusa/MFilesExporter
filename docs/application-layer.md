# Application Layer

The application layer coordinates the export process. It sits between the
Domain (business types) and the outer layers (Persistence, Export, Reporting,
Infrastructure). It owns:

- **Ports** — interfaces implemented by outer adapters.
- **Use cases** — CQRS-style commands and queries with dedicated handlers.
- **Result types** — explicit success/failure returned by every use case.
- **Dispatcher** — one abstraction consumers depend on to invoke handlers.
- **Cross-cutting behavior** — logging/correlation via a dispatcher decorator.
- **Options snapshots** — validated at host build time.

The layer does not perform I/O directly. Every I/O boundary is a port.

---

## Shape at a glance

```
                       ┌──────────────────────────┐
    consumers ────►    │ IApplicationDispatcher    │  ← single entry point
                       └────────────┬─────────────┘
                                    │
                                    ▼
                       ┌──────────────────────────┐
                       │ LoggingApplicationDispatcher (decorator)
                       └────────────┬─────────────┘
                                    │
                                    ▼
                       ┌──────────────────────────┐
                       │ ApplicationDispatcher     │  ← resolves handler by generic type
                       └────────────┬─────────────┘
                                    │
                          ┌─────────┴──────────┐
                          ▼                    ▼
                ┌──────────────────┐  ┌──────────────────┐
                │ ICommandHandler  │  │ IQueryHandler    │  ← concrete handlers per use case
                └────────┬─────────┘  └────────┬─────────┘
                         │                     │
                         ▼                     ▼
                     Ports (Application.Abstractions)
                         │                     │
                         ▼                     ▼
                Adapters (Persistence, Export, Reporting)
```

---

## Result types

Every use case returns `ApplicationResult` or `ApplicationResult<T>`. These
are `readonly record struct` value types — the happy path never allocates.

```csharp
public readonly record struct ApplicationResult
public readonly record struct ApplicationResult<T>
```

Failures carry one or more `ApplicationError` records. Each error has:
- `Code` — machine-readable, stable (e.g. `JOB_ID_REQUIRED`, `NOT_FOUND`).
- `Message` — human-readable.
- `Kind` — `Failure | Validation | NotFound | Forbidden | Conflict | Transient | Unexpected`, so callers can route responses (e.g. HTTP 404 vs 409 vs 500).

Reading `.Value` on a failed generic result throws — callers must
consult `IsSuccess` first.

---

## Dispatcher and CQRS

Three marker interfaces:

```csharp
interface ICommand                       { }   // void success
interface ICommand<TResult>              { }   // returns payload
interface IQuery<TResult>                { }   // read-only, returns payload
```

Each is paired with a handler:

```csharp
ICommandHandler<TCommand>
ICommandHandler<TCommand, TResult>
IQueryHandler<TQuery, TResult>
```

The dispatcher (`IApplicationDispatcher`) resolves the correct handler by
generic type — no reflection over method names, no runtime scanning. This
keeps stack traces clean and startup fast.

The default implementation is decorated at DI time with
`LoggingApplicationDispatcher`, which:
- Assigns a fresh `CorrelationId` per invocation.
- Opens a Serilog scope with the correlation id + operation name.
- Emits begin/success/failure log lines with timings.

Add more cross-cutting concerns (metrics, tracing, retry) by wrapping the
same interface — no changes to handlers.

---

## Use case catalog

### Jobs
| Type | Kind | Handler responsibility |
|---|---|---|
| `StartExportJobCommand` | ICommand<long> | Creates a job in the tracking DB, marks it Running, returns the assigned id. |
| `CompleteExportJobCommand` | ICommand | Transitions to Completed / Failed / Cancelled. |
| `CancelExportJobCommand` | ICommand | Convenience wrapper — delegates to CompleteExportJob with terminal=Cancelled. |
| `GetJobStatusQuery` | IQuery<ExportJobRecord> | Read a single job row. |
| `GetJobStatisticsQuery` | IQuery<JobStatisticsView> | Job header + latest progress snapshot. |

### Workers
| Type | Kind | Handler responsibility |
|---|---|---|
| `RegisterWorkerCommand` | ICommand<long> | Register a worker under a job. |
| `HeartbeatWorkerCommand` | ICommand | Advance the worker's heartbeat and status. |
| `StopWorkerCommand` | ICommand | Mark a worker Stopped. |

### Progress
| Type | Kind | Handler responsibility |
|---|---|---|
| `SaveCheckpointCommand` | ICommand<bool> | Monotonic checkpoint upsert; result indicates whether it advanced. |
| `RecordProgressSnapshotCommand` | ICommand | Append a single progress snapshot. |
| `GetLatestProgressQuery` | IQuery<ExportProgressRecord> | Most-recent snapshot for a job. |
| `GetActiveCheckpointQuery` | IQuery<ExportCheckpointRecord> | Current active checkpoint for (job, partition). |

### Errors
| Type | Kind | Handler responsibility |
|---|---|---|
| `LogErrorCommand` | ICommand<long> | Insert an error and return its id. |
| `ResolveErrorCommand` | ICommand | Terminal resolve/ignore transition. |
| `GetRecentAuditQuery` | IQuery<IReadOnlyList<ExportAuditRecord>> | Last N audit rows for a job. |

### Pipeline
| Type | Kind | Handler responsibility |
|---|---|---|
| `RunExportCommand` | ICommand<RunExportSummary> | Full lifecycle: StartJob → RegisterWorker → RunPipeline → StopWorker → CompleteJob. Always transitions to a terminal state, even on cancellation. Populates `IJobContext` so the checkpoint engine's tracking-DB layer can attribute writes to a real `ExportJobId`. |

`ExportHostedService` dispatches `RunExportCommand` at startup — it is
the single entry point for a run. The CQRS pipeline is authoritative;
there is no direct-call orchestrator any more.

---

## Validation strategy

Two-tier as in the domain layer:

1. **Structural validation** inside the handler. Cheap, per-property, code
   like `if (id <= 0) return ApplicationResult.Failure(...)`. This lives at
   the handler because it is command-shape-specific and returns a
   `ValidationErrorKind` for callers.
2. **Business-rule validation** deferred to the domain (`ExportConfiguration.Validate()`,
   `ExportJobStatusTransitions.IsAllowed(...)`). The handler consults the
   domain method and converts the result to `ApplicationResult`.

Handlers never throw for validation failures. Exceptions are reserved for
either genuine bugs (mapped to `ApplicationErrorKind.Unexpected`) or
downstream I/O failures (mapped to `Transient` when they might succeed on
retry).

---

## Error handling contract

Every handler follows the same three-step pattern:

```csharp
public async Task<ApplicationResult<T>> HandleAsync(TCommand c, CancellationToken ct)
{
    // 1. Validate — return early with Validation errors.
    // 2. Do the work — call ports.
    // 3. Map exceptions:
    //    - OperationCanceledException: rethrow so the pipeline honours cancellation.
    //    - Transient (SqlException / IOException):  return Transient error.
    //    - Everything else:                         return Unexpected error + log.
}
```

`OperationCanceledException` is **never** wrapped in a result — cancellation
must propagate. All other exceptions are converted so callers can react
programmatically without try/catch around every dispatch.

---

## DI registration

The application layer exposes exactly one extension:

```csharp
services.AddExporterApplication();
```

This registers:
- `ApplicationDispatcher` (inner) + `LoggingApplicationDispatcher`
  (decorator) as `IApplicationDispatcher`.
- Every command handler and query handler as their interface.
- `RunExportHandler` for the top-level orchestration command.
- `IJobContext` (singleton) — ambient scope populated by
  `RunExportHandler` and read by the checkpoint engine.

Handlers are registered as singletons because they hold no per-request
state; ports they depend on (repositories) are themselves singletons.

---

## Testing patterns

- **Handlers are the unit under test.** Substitute the port with NSubstitute
  and drive the handler with a command.
- **Result assertions** use FluentAssertions on `IsSuccess` / `IsFailure` /
  `PrimaryError` / `Errors`. `.Value` should never be read from a
  potentially-failed result.
- **Dispatcher tests** live under `Application/ApplicationDispatcherTests`
  and confirm handler resolution + missing-handler behaviour.

Sample handler test structure lives under
`tests/MFilesExporter.Tests/Application/`.

---

## What the application layer intentionally does NOT contain

- **No SQL, no filesystem, no HTTP.** Those are the outer layers.
- **No dependency on Polly, Dapper, Microsoft.Data.SqlClient.**
  `IResiliencePipelineProvider` is the abstraction.
- **No shared mutable state.** Handlers are stateless; each invocation is a
  pure function of `command × ports`.
- **No coupling to a specific mediator library.** The dispatcher is 60
  lines and lives in this project.
