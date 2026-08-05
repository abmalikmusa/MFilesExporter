# Coding Conventions

## Language and target

- .NET 9 (`net9.0`), C# `latest`.
- `Nullable` and `ImplicitUsings` enabled everywhere via `Directory.Build.props`.
- `TreatWarningsAsErrors=true` in every project.

## Namespaces

- File-scoped, e.g., `namespace MFilesExporter.Application.UseCases;`.
- Mirror the folder path exactly.

## Types

- `sealed class` by default. Only unseal when a design intentionally supports
  subclassing.
- `record` for pure data. `readonly record struct` for small value types
  (`DocumentFileVersionKey`, `IdempotencyKey`).
- `required` properties on records to make invalid states unrepresentable.

## Methods and members

- Public methods explicit-return-typed (`Task<T>` not `Task<dynamic>` etc.).
- `async` methods end with `Async` when they return `Task` or `ValueTask`.
- `ConfigureAwait(false)` on every await in library code.
- Cancellation tokens accepted as the last parameter, never optional.

## Guard clauses

- Domain constructors and public entry points guard their inputs using either
  `ArgumentNullException.ThrowIfNull(...)`, `ArgumentException.ThrowIfNullOrWhiteSpace(...)`,
  `ArgumentOutOfRangeException.ThrowIfNegative(...)`, or `MFilesExporter.Shared.Guards.Guard`.
- Internal methods do not repeat guards their callers have already applied.

## Errors

- **Domain-level failures** raise a `DomainException` subclass (e.g.
  `DocumentContentMissingException`). These are non-retryable by definition.
- **Transient adapter failures** propagate typed exceptions
  (`SqlException`, `IOException`) so the resilience layer can classify them.
- Never catch `Exception` in library code except at pipeline boundaries where
  the outcome is recorded and control returns.

## Concurrency

- Bounded `Channel<T>` for stage-to-stage flow; unbounded channels are
  forbidden in the pipeline.
- Shared mutable state is either immutable, guarded by a lock, or on a
  single-consumer channel — never left to memory-model chance.
- No `sync-over-async`. Never `.Result`, never `.Wait()`, never
  `.GetAwaiter().GetResult()` outside `Program.cs`.

## Logging

- Structured logging only. Never string-concat data into a message template.
- Log-message parameters use PascalCase names: `{DocumentFilePart}`,
  `{BytesWritten}`.

## Testing

- xUnit + FluentAssertions + NSubstitute (for the rare true mock).
- Test naming: `MethodOrScenario_ExpectedOutcome`.
- Unit tests use in-memory fakes; integration tests use real backends.
- Architecture rules under `MFilesExporter.Tests/Architecture/` — must pass
  before merge.

## Documentation

- Public ports carry `<summary>` XML doc comments.
- No comments that restate the code. Comments explain *why*, not *what*.
- Adapters that involve tricky ownership (e.g. streaming SqlDataReader)
  carry a paragraph explaining the disposal contract.
