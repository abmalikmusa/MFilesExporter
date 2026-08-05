# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:9.0-jammy AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY MFilesExporter.sln ./
COPY src/MFilesExporter.Domain/MFilesExporter.Domain.csproj             src/MFilesExporter.Domain/
COPY src/MFilesExporter.Shared/MFilesExporter.Shared.csproj             src/MFilesExporter.Shared/
COPY src/MFilesExporter.Configuration/MFilesExporter.Configuration.csproj   src/MFilesExporter.Configuration/
COPY src/MFilesExporter.Logging/MFilesExporter.Logging.csproj           src/MFilesExporter.Logging/
COPY src/MFilesExporter.Application/MFilesExporter.Application.csproj   src/MFilesExporter.Application/
COPY src/MFilesExporter.Persistence/MFilesExporter.Persistence.csproj   src/MFilesExporter.Persistence/
COPY src/MFilesExporter.Export/MFilesExporter.Export.csproj             src/MFilesExporter.Export/
COPY src/MFilesExporter.Reporting/MFilesExporter.Reporting.csproj       src/MFilesExporter.Reporting/
COPY src/MFilesExporter.Infrastructure/MFilesExporter.Infrastructure.csproj src/MFilesExporter.Infrastructure/
COPY src/MFilesExporter.Console/MFilesExporter.Console.csproj           src/MFilesExporter.Console/
RUN dotnet restore src/MFilesExporter.Console/MFilesExporter.Console.csproj

COPY src/ src/
RUN dotnet publish src/MFilesExporter.Console/MFilesExporter.Console.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:9.0-jammy AS runtime
RUN groupadd --gid 10001 mfx && \
    useradd --uid 10001 --gid mfx --shell /bin/false --create-home mfx && \
    apt-get update && \
    apt-get install -y --no-install-recommends tini ca-certificates && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./
RUN mkdir -p /app/export-output/documents /app/export-output/manifests /app/logs && chown -R mfx:mfx /app
USER mfx

ENV DOTNET_gcServer=1 \
    DOTNET_TieredCompilation=1 \
    DOTNET_EnableDiagnostics=0 \
    MFILESEXPORTER_Exporter__Storage__RootPath=/app/export-output/documents \
    MFILESEXPORTER_Exporter__Storage__ManifestPath=/app/export-output/manifests \
    MFILESEXPORTER_Exporter__StateStore__ConnectionString=/app/export-output/state.db

EXPOSE 9464

ENTRYPOINT ["/usr/bin/tini", "--", "dotnet", "MFilesExporter.Console.dll"]
