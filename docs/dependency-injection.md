# Dependency Injection

## Container

The Microsoft.Extensions.DependencyInjection container ships with .NET is used
as-is. No third-party container.

## Wiring surface

Each project that has services exposes exactly one extension method under its
`<Project>.DependencyInjection` namespace:

| Project | Extension |
|---|---|
| `MFilesExporter.Configuration` | `AddExporterConfiguration(IConfiguration)` |
| `MFilesExporter.Application` | `AddExporterApplication()` |
| `MFilesExporter.Persistence` | `AddExporterPersistence()` |
| `MFilesExporter.Export` | `AddExporterExport()` |
| `MFilesExporter.Reporting` | `AddExporterReporting()` |
| `MFilesExporter.Infrastructure` | `AddExporterInfrastructure(TelemetryOptions)` |

The Console composition root calls each in a fixed order:

```csharp
builder.Services.AddExporterConfiguration(builder.Configuration);   // Options first
builder.Services.AddSerilog(...);                                    // Logging next
builder.Services.AddExporterInfrastructure(telemetry);               // Clock, resilience, OTel, health
builder.Services.AddExporterPersistence();                           // Adapters for source + state
builder.Services.AddExporterExport();                                // Pipeline + sink + manifest
builder.Services.AddExporterReporting();                             // Progress + summary
builder.Services.AddExporterApplication();                           // Orchestrator
builder.Services.AddHostedService<ExportHostedService>();            // Entry point
```

## Rules

1. **Every application port has exactly one adapter registration.** If two
   adapters exist (e.g. two `IExportStateStore` implementations), the composition
   root picks by configuration — never by DI order.
2. **Concrete implementations of internal-only ports are marked `internal`.**
   Ports themselves are `public` because they cross assembly boundaries.
3. **All services are `AddSingleton` unless they hold per-operation state.**
   Options objects are singletons via `IOptions<T>.Value`.
4. **Hosted services are wired via `AddHostedService<T>()`** so the framework
   owns their lifetime.
5. **No constructor takes `IServiceProvider`.** Resolve everything through
   typed constructor injection.
