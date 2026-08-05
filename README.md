# MFilesExporter

Production-grade M-Files document export platform, .NET 9.

## Solution layout

```
MFilesExporter.sln
├── src/
│   ├── MFilesExporter.Domain            # Pure domain types
│   ├── MFilesExporter.Shared            # Cross-cutting primitives
│   ├── MFilesExporter.Configuration     # Options + FluentValidation
│   ├── MFilesExporter.Logging           # Serilog composition
│   ├── MFilesExporter.Application       # Use cases + ports
│   ├── MFilesExporter.Persistence       # SQL + SQLite adapters
│   ├── MFilesExporter.Export            # Pipeline + sink + manifest
│   ├── MFilesExporter.Reporting         # Progress + summary + metrics
│   ├── MFilesExporter.Infrastructure    # Resilience + telemetry + health
│   └── MFilesExporter.Console           # Composition root (entry point)
└── tests/
    └── MFilesExporter.Tests             # Unit + integration + architecture
```

## Build and run

```bash
cd MFilesExporter
dotnet restore
dotnet build -c Release
dotnet test  -c Release

# Edit src/MFilesExporter.Console/appsettings.json first, then:
dotnet run --project src/MFilesExporter.Console -c Release
```

## Docs

- [Project responsibilities and layering](docs/architecture.md)
- [Configuration reference](docs/configuration.md)
- [Coding conventions](docs/conventions.md)
- [DI wiring guide](docs/dependency-injection.md)
