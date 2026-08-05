# Architecture

## Project responsibilities

| Project | Responsibility | Key types |
|---|---|---|
| **MFilesExporter.Domain** | Pure business types. No external references. | `DocumentDescriptor`, `DocumentFileVersionKey`, `DataFileVersionKey`, `IdempotencyKey`, `ExportOutcome`, `ExportStatus`, `ExportProgress` |
| **MFilesExporter.Shared** | Cross-cutting primitives that are safe from every layer. | `Guard`, `CollectionExtensions`, `FileSystemHelpers` |
| **MFilesExporter.Configuration** | Strongly-typed options and their FluentValidation validators. | `ExporterOptions`, `MFilesSourceOptions`, `StorageOptions`, `PipelineOptions`, `ResilienceOptions`, `StateStoreOptions`, `TelemetryOptions`, `ExporterOptionsValidator` |
| **MFilesExporter.Logging** | Serilog composition and host integration. | `SerilogBootstrap` |
| **MFilesExporter.Application** | Use cases + ports. No I/O, no adapters. | `ExportOrchestrator`, `IDocumentEnumerator`, `IDocumentContentReader`, `IDocumentSink`, `IExportStateStore`, `IManifestWriter`, `IExportPipeline`, `IProgressReporter`, `IClock`, `IChecksumCalculator`, `IResiliencePipelineProvider` |
| **MFilesExporter.Persistence** | Adapters for durable stores. | `MFilesQueries`, `SqlConnectionFactory`, `MFilesSqlDocumentEnumerator`, `MFilesSqlContentReader`, `SqliteStateStore` |
| **MFilesExporter.Export** | Streaming extraction machinery. | `PipelineChannels`, `ProducerStage`, `ContentReaderStage`, `SinkStage`, `OutcomeCollectorStage`, `ExportPipeline`, `PathBuilder`, `FileSystemDocumentSink`, `JsonLinesManifestWriter`, `Sha256ChecksumCalculator`, `PipelineTelemetry` |
| **MFilesExporter.Reporting** | Progress and summary reporting. | `LoggingProgressReporter`, `ProgressPublisherHostedService`, `RunSummaryReporter` |
| **MFilesExporter.Infrastructure** | Cross-cutting adapters (resilience, clock, telemetry, health). | `ResiliencePipelineFactory`, `SystemClock`, `OpenTelemetryHostingExtensions`, `MFilesSqlHealthCheck`, `StateStoreHealthCheck`, `StorageHealthCheck` |
| **MFilesExporter.Console** | Composition root and process host. | `Program.cs`, `ExportHostedService` |
| **MFilesExporter.Tests** | Unit, integration, and architecture-rule tests. | `IdempotencyKeyTests`, `MFilesQueriesTests`, `SqliteStateStoreTests`, `FileSystemDocumentSinkTests`, `ArchitectureRulesTests` |

## Dependency direction

```
Domain <- Shared <- Configuration <- Logging
   ^                       ^
   |                       |
Application  ------  Infrastructure
   ^  ^  ^                 ^
   |  |  |                 |
Persistence  Export  Reporting
   \    |    /             |
    \   |   /              |
     Console --------------+
     (composition root)
```

- **Inward only** — no arrow ever points outward from an inner layer to an outer one.
- **Peer isolation** — Persistence / Export / Reporting do not reference each other. They communicate through Application ports.
- **Composition root** — only `MFilesExporter.Console` sees every project. Nothing else does.

## Namespaces

Namespaces mirror the folder structure and are always file-scoped:

```
MFilesExporter.<Project>[.<Subfolder>]
```

Examples:

- `MFilesExporter.Domain.Documents`
- `MFilesExporter.Application.Abstractions`
- `MFilesExporter.Persistence.MFiles`
- `MFilesExporter.Export.Pipeline`
- `MFilesExporter.Infrastructure.Resilience`

## NuGet packages

Managed centrally via `Directory.Packages.props`. Categories:

- **Data access** — `Microsoft.Data.SqlClient`, `Microsoft.Data.Sqlite`, `Dapper`
- **Hosting/DI/Options** — `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.DependencyInjection*`, `Microsoft.Extensions.Options*`, `Microsoft.Extensions.Configuration*`
- **Logging** — `Serilog.*` (bootstrap + host + async + file + console + compact JSON + enrichers)
- **Telemetry** — `OpenTelemetry`, `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.Prometheus.HttpListener`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.Runtime`
- **Resilience** — `Polly`
- **Validation** — `FluentValidation`
- **Testing** — `xunit`, `xunit.runner.visualstudio`, `FluentAssertions`, `NSubstitute`, `coverlet.collector`, `NetArchTest.Rules`

## Architecture validation

`MFilesExporter.Tests/Architecture/ArchitectureRulesTests.cs` uses
[NetArchTest.Rules](https://github.com/BenMorris/NetArchTest) to fail the build if
any of these invariants are broken:

- `Domain` has no project dependencies.
- `Application` does not reference `Persistence`, `Export`, `Reporting`,
  `Infrastructure`, `Console`, or `Logging`.
- `Persistence`, `Export`, `Reporting`, `Infrastructure` do not reference each other.
- `Configuration` and `Logging` are leaf projects (no business references).

Run: `dotnet test tests/MFilesExporter.Tests`
