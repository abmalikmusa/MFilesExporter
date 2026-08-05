using MFilesExporter.Configuration.Options;

namespace MFilesExporter.Persistence.Tracking.Sql;

/// <summary>
/// Central place to compute the value passed as @ActorName to every proc.
/// Precedence: configuration override → machine name.
/// </summary>
internal static class ActorContext
{
    public static string Resolve(TrackingDatabaseOptions options) =>
        !string.IsNullOrWhiteSpace(options.ActorNameOverride)
            ? options.ActorNameOverride!
            : Environment.MachineName;
}
