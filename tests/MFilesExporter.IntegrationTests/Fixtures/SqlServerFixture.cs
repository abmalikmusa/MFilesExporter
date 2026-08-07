using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace MFilesExporter.IntegrationTests.Fixtures;

/// <summary>
/// Boots a SQL Server 2022 container once for the whole test collection,
/// provisions both databases the exporter needs (source vault + tracking DB),
/// and exposes their connection strings.
/// </summary>
/// <remarks>
/// Container startup takes ~30 s on first pull and ~10 s on subsequent runs.
/// Set the environment variable <c>MFILESEXPORTER_TESTS_REUSE_CONTAINER=1</c>
/// to enable Testcontainers' reuse mode when iterating locally.
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string SaPassword = "Str0ngP@ssword!2026";

    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .WithPassword(SaPassword)
        .WithEnvironment("ACCEPT_EULA", "Y")
        .Build();

    public string SourceConnectionString { get; private set; } = string.Empty;
    public string TrackingConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);

        // The Testcontainers-provided connection string points at `master`.
        // Build two more, one per database, after we've provisioned them.
        var masterCs = _container.GetConnectionString();

        await ExecuteAsync(masterCs, """
            CREATE DATABASE MFilesVault;
            CREATE DATABASE MFilesExportTracking;
        """).ConfigureAwait(false);

        SourceConnectionString   = SwitchDatabase(masterCs, "MFilesVault");
        TrackingConnectionString = SwitchDatabase(masterCs, "MFilesExportTracking");

        await ProvisionTrackingSchemaAsync().ConfigureAwait(false);
        await ProvisionVaultSchemaAsync().ConfigureAwait(false);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    // -------------------------------------------------------------------
    // Schema provisioning
    // -------------------------------------------------------------------

    private async Task ProvisionTrackingSchemaAsync()
    {
        // 00-database.sql assumes it can CREATE DATABASE with a specific
        // instance data path. In the container the database is already
        // created by us; apply only the schema + filegroup portions inline
        // and then run 10+ scripts.
        await ExecuteBatchesAsync(TrackingConnectionString, """
            IF SCHEMA_ID(N'archive') IS NULL EXEC(N'CREATE SCHEMA [archive] AUTHORIZATION dbo;');
            IF SCHEMA_ID(N'ops')     IS NULL EXEC(N'CREATE SCHEMA [ops]     AUTHORIZATION dbo;');
            GO
            -- Role stubs so GRANT ... TO [ExporterWriterRole]/[ExporterReaderRole] scattered
            -- through 15/35/70/71 succeed. We skip 50-security.sql (logins) since the container
            -- connects as sa.
            IF DATABASE_PRINCIPAL_ID(N'ExporterWriterRole') IS NULL
                EXEC(N'CREATE ROLE [ExporterWriterRole] AUTHORIZATION [dbo];');
            IF DATABASE_PRINCIPAL_ID(N'ExporterReaderRole') IS NULL
                EXEC(N'CREATE ROLE [ExporterReaderRole] AUTHORIZATION [dbo];');
            GO
            IF NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE name = N'ArchiveFG')
            BEGIN
                ALTER DATABASE [MFilesExportTracking] ADD FILEGROUP [ArchiveFG];

                DECLARE @path NVARCHAR(4000) =
                    CONVERT(NVARCHAR(4000), SERVERPROPERTY('InstanceDefaultDataPath'))
                    + N'MFilesExportTracking_Archive.ndf';

                DECLARE @sql NVARCHAR(MAX) = N'
                    ALTER DATABASE [MFilesExportTracking] ADD FILE
                    (
                        NAME = N''MFilesExportTracking_Archive'',
                        FILENAME = N''' + @path + N''',
                        SIZE = 64 MB,
                        MAXSIZE = UNLIMITED,
                        FILEGROWTH = 64 MB
                    ) TO FILEGROUP [ArchiveFG];';

                EXEC sys.sp_executesql @sql;
            END
        """).ConfigureAwait(false);

        var dbDir = Path.Combine(AppContext.BaseDirectory, "database");
        var files = Directory.GetFiles(dbDir, "*.sql")
                             .Where(f =>
                             {
                                 var name = Path.GetFileName(f);
                                 // 00 handled inline above; 50 provisions logins/roles the
                                 // exporter role would need — irrelevant when we run as sa.
                                 return !name.StartsWith("00-", StringComparison.Ordinal)
                                     && !name.StartsWith("50-", StringComparison.Ordinal);
                             })
                             .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal);

        foreach (var file in files)
        {
            var sql = await File.ReadAllTextAsync(file).ConfigureAwait(false);
            try
            {
                await ExecuteBatchesAsync(TrackingConnectionString, sql).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to apply {Path.GetFileName(file)}: {ex.Message}", ex);
            }
        }
    }

    private async Task ProvisionVaultSchemaAsync()
    {
        // Synthetic vault schema aligned with MFilesQueries.EnumerationQuery and ContentQuery.
        await ExecuteAsync(SourceConnectionString, """
            CREATE TABLE dbo.DOCUMENTFILEVERSION
            (
                ID_DOCUMENTFILEPART BIGINT       NOT NULL,
                ID_VERSIONPART      INT          NOT NULL,
                DATAFILEVERSION     BIGINT       NOT NULL,
                TITLE               NVARCHAR(255) NOT NULL,
                EXTENSION           NVARCHAR(32)  NOT NULL,
                CONSTRAINT PK_DOCUMENTFILEVERSION
                    PRIMARY KEY (ID_DOCUMENTFILEPART, ID_VERSIONPART, DATAFILEVERSION)
            );

            CREATE TABLE dbo.DATAFILEVERSION
            (
                ID_DOCUMENTFILEPART BIGINT       NOT NULL,
                ID_DATAFILEVERSION  BIGINT       NOT NULL,
                LOGICALFILESIZE     BIGINT       NOT NULL,
                PHYSICALFILESIZE    BIGINT       NOT NULL,
                LASTWRITETIME       DATETIME2(3) NOT NULL,
                UPLOADCOMMITTED     BIT          NOT NULL,
                CONSTRAINT PK_DATAFILEVERSION
                    PRIMARY KEY (ID_DOCUMENTFILEPART, ID_DATAFILEVERSION)
            );

            CREATE TABLE dbo.DATAFILEVERSION_BYTES
            (
                ID_DOCUMENTFILEPART BIGINT         NOT NULL,
                ID_DATAFILEVERSION  BIGINT         NOT NULL,
                DATA                VARBINARY(MAX) NOT NULL,
                CONSTRAINT PK_DATAFILEVERSION_BYTES
                    PRIMARY KEY (ID_DOCUMENTFILEPART, ID_DATAFILEVERSION)
            );
        """).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static string SwitchDatabase(string cs, string database)
    {
        var builder = new SqlConnectionStringBuilder(cs)
        {
            InitialCatalog = database,
        };
        return builder.ConnectionString;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static readonly System.Text.RegularExpressions.Regex GoSplitter =
        new(@"^\s*GO\s*(?:--.*)?$",
            System.Text.RegularExpressions.RegexOptions.Multiline
          | System.Text.RegularExpressions.RegexOptions.IgnoreCase
          | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static async Task ExecuteBatchesAsync(string connectionString, string sql)
    {
        // GO is a batch separator, not a T-SQL statement. Split on any line
        // whose only non-comment content is "GO" — tolerant of leading /
        // trailing whitespace and \r\n vs \n.
        var batches = GoSplitter.Split(sql);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        foreach (var raw in batches)
        {
            var batch = raw.Trim();
            if (batch.Length == 0) continue;
            await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}

[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture> { }
