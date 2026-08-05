using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Abstractions.WorkClaiming;
using MFilesExporter.Persistence.MFiles;
using MFilesExporter.Persistence.MFiles.Blobs;
using MFilesExporter.Persistence.MFiles.Streaming;
using MFilesExporter.Persistence.State;
using MFilesExporter.Persistence.Tracking.Sql;
using Microsoft.Extensions.DependencyInjection;

namespace MFilesExporter.Persistence.DependencyInjection;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddExporterPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // M-Files vault access (read-only).
        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<IDocumentEnumerator, MFilesSqlDocumentEnumerator>();
        services.AddSingleton<IDocumentContentReader, MFilesSqlContentReader>();
        // Unified streaming engine — the consolidated surface over the two
        // vault adapters above. Prefer ISqlStreamingEngine for new callers.
        services.AddSingleton<ISqlStreamingEngine, SqlStreamingEngine>();

        // Binary Object Reader — general-purpose varbinary(max) copier with
        // checksum, progress, and validation. Used by verification tools,
        // ad-hoc utilities, and any sink that needs inline hashing.
        services.AddSingleton<IBinaryObjectReader, BinaryObjectReader>();

        // Work-claim store (SQL Server queue).
        services.AddSingleton<IWorkClaimStore, SqlWorkClaimStore>();

        // Local SQLite state store (default for single-node deployments).
        services.AddSingleton<SqliteStateStore>();
        services.AddSingleton<IExportStateStore>(sp => sp.GetRequiredService<SqliteStateStore>());

        // SQL Server tracking DB — seven repositories over stored procedures + TVPs.
        services.AddSingleton<ITrackingSqlConnectionFactory, TrackingSqlConnectionFactory>();
        services.AddSingleton<SqlExecutor>();
        services.AddSingleton<IExportJobRepository,        SqlServerJobRepository>();
        services.AddSingleton<IExportWorkerRepository,     SqlServerWorkerRepository>();
        services.AddSingleton<IExportProgressRepository,   SqlServerProgressRepository>();
        services.AddSingleton<IExportMetricRepository,     SqlServerMetricRepository>();
        services.AddSingleton<IExportErrorRepository,      SqlServerErrorRepository>();
        services.AddSingleton<IExportCheckpointRepository, SqlServerCheckpointRepository>();
        services.AddSingleton<IExportAuditRepository,      SqlServerAuditRepository>();

        return services;
    }
}
