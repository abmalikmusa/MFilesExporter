using System.Data;
using System.Runtime.CompilerServices;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Application.Abstractions.Retry;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Persistence.MFiles;

internal sealed class MFilesSqlDocumentEnumerator : IDocumentEnumerator
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly MFilesSourceOptions _options;
    private readonly IRetryExecutor _resilience;
    private readonly ILogger<MFilesSqlDocumentEnumerator> _logger;

    public MFilesSqlDocumentEnumerator(
        ISqlConnectionFactory connectionFactory,
        MFilesSourceOptions options,
        IRetryExecutor resilience,
        ILogger<MFilesSqlDocumentEnumerator> logger)
    {
        _connectionFactory = connectionFactory;
        _options = options;
        _resilience = resilience;
        _logger = logger;
    }

    public async IAsyncEnumerable<DocumentDescriptor> EnumerateAsync(
        DocumentFileVersionKey exclusiveLowerBound,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = exclusiveLowerBound;
        var sql = MFilesQueries.EnumerationQuery(_options.Tables, _options.UseReadUncommittedForEnumeration);
        var batchSize = _options.EnumerationBatchSize;

        while (true)
        {
            var batch = await _resilience.ExecuteAsync(
                RetryOperationNames.SqlRead,
                ct => new ValueTask<IReadOnlyList<DocumentDescriptor>>(FetchBatchAsync(sql, cursor, batchSize, ct)),
                cancellationToken).ConfigureAwait(false);

            if (batch.Count == 0) yield break;

            foreach (var d in batch)
            {
                yield return d;
            }

            cursor = batch[^1].DocumentFileVersionKey;
            if (batch.Count < batchSize) yield break;
        }
    }

    public async Task<long> CountRemainingAsync(
        DocumentFileVersionKey exclusiveLowerBound,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            MFilesQueries.RemainingCountQuery(_options.Tables, _options.UseReadUncommittedForEnumeration),
            connection)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };
        command.Parameters.Add("@LastDocumentFilePartId", SqlDbType.BigInt).Value = exclusiveLowerBound.DocumentFilePartId;
        command.Parameters.Add("@LastVersionPartId", SqlDbType.BigInt).Value = exclusiveLowerBound.VersionPartId;

        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is long v ? v : Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<DocumentDescriptor>> FetchBatchAsync(
        string sql,
        DocumentFileVersionKey cursor,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var results = new List<DocumentDescriptor>(batchSize);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };
        command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = batchSize;
        command.Parameters.Add("@LastDocumentFilePartId", SqlDbType.BigInt).Value = cursor.DocumentFilePartId;
        command.Parameters.Add("@LastVersionPartId", SqlDbType.BigInt).Value = cursor.VersionPartId;

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleResult | CommandBehavior.SequentialAccess,
            cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var docPart = reader.GetInt64(0);
            // ID_VERSIONPART is INT in the vault schema.
            var verPart = (long)reader.GetInt32(1);
            var title = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                ? string.Empty : reader.GetString(2);
            var ext = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
                ? string.Empty : reader.GetString(3);
            var dataFileVersion = reader.GetInt64(4);
            var logicalSize = reader.GetInt64(5);
            var physicalSize = reader.GetInt64(6);
            var lastWrite = await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                ? DateTime.UnixEpoch : reader.GetDateTime(7);

            results.Add(new DocumentDescriptor(
                new DocumentFileVersionKey(docPart, verPart),
                new DataFileVersionKey(docPart, dataFileVersion),
                title, ext, logicalSize, physicalSize,
                DateTime.SpecifyKind(lastWrite, DateTimeKind.Utc)));
        }

        _logger.LogDebug("Fetched {Count} rows past {Cursor}", results.Count, cursor);
        return results;
    }
}
