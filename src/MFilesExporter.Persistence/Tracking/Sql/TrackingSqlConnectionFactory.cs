using MFilesExporter.Configuration.Options;
using Microsoft.Data.SqlClient;

namespace MFilesExporter.Persistence.Tracking.Sql;

/// <summary>
/// Opens a <see cref="SqlConnection"/> against the MFilesExportTracking
/// database. Distinct from the vault's <see cref="MFiles.ISqlConnectionFactory"/>
/// so the two databases can have independent credentials, timeouts, and
/// connection pools.
/// </summary>
public interface ITrackingSqlConnectionFactory
{
    Task<SqlConnection> OpenAsync(CancellationToken cancellationToken);
}

internal sealed class TrackingSqlConnectionFactory : ITrackingSqlConnectionFactory
{
    private readonly TrackingDatabaseOptions _options;

    public TrackingSqlConnectionFactory(TrackingDatabaseOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
