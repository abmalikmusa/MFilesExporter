using System.Data;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Domain.Exceptions;
using Microsoft.Data.SqlClient;

namespace MFilesExporter.Persistence.MFiles;

internal sealed class MFilesSqlContentReader : IDocumentContentReader
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly MFilesSourceOptions _options;

    public MFilesSqlContentReader(ISqlConnectionFactory connectionFactory, MFilesSourceOptions options)
    {
        _connectionFactory = connectionFactory;
        _options = options;
    }

    public async Task<DocumentContentStream> OpenAsync(DataFileVersionKey key, CancellationToken cancellationToken)
    {
        SqlConnection? connection = null;
        SqlCommand? command = null;
        SqlDataReader? reader = null;

        try
        {
            connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            command = new SqlCommand(MFilesQueries.ContentQuery(_options.Tables), connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds,
            };
            command.Parameters.Add("@DocumentFilePartId", SqlDbType.BigInt).Value = key.DocumentFilePartId;
            command.Parameters.Add("@DataFileVersionId", SqlDbType.BigInt).Value = key.DataFileVersionId;

            reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleResult | CommandBehavior.SingleRow | CommandBehavior.SequentialAccess,
                cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new DocumentContentMissingException(key);
            }
            if (await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new DocumentContentMissingException(key);
            }

            // Explicit GetBytes()-based streaming reader. Never allocates the
            // full BLOB; each Read() pulls the next chunk from the TDS buffer
            // under SequentialAccess mode.
            Stream stream = new SqlBytesReadStream(reader, ordinal: 0);

            // Transfer ownership to the returned DocumentContentStream.
            var readerRef = reader;
            var commandRef = command;
            var connectionRef = connection;
            reader = null;
            command = null;
            connection = null;

            return new DocumentContentStream(
                stream,
                length: -1,
                dispose: async () =>
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    await readerRef.DisposeAsync().ConfigureAwait(false);
                    await commandRef.DisposeAsync().ConfigureAwait(false);
                    await connectionRef.DisposeAsync().ConfigureAwait(false);
                });
        }
        catch
        {
            if (reader is not null) await reader.DisposeAsync().ConfigureAwait(false);
            if (command is not null) await command.DisposeAsync().ConfigureAwait(false);
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
