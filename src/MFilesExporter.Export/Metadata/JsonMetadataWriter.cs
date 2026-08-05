using System.Globalization;
using System.Text;
using System.Text.Json;
using MFilesExporter.Configuration.Options;

namespace MFilesExporter.Export.Metadata;

/// <summary>
/// Streaming writer for <c>metadata.json</c> — a well-formed JSON array
/// with one <see cref="MetadataRecord"/> per element, written via
/// <see cref="Utf8JsonWriter"/> so records never materialize into a
/// combined in-memory structure.
///
/// The envelope shape (with schema version + producer) is emitted before
/// the array so EDMS-migration tools have a stable header:
/// <code>
/// {
///   "schemaVersion": "1.0.0",
///   "schemaId":      "seamfix.mfiles-exporter.metadata/1.0",
///   "generator":     "MFilesExporter",
///   "records": [
///     { "documentPartId": 1, ... },
///     { ... }
///   ]
/// }
/// </code>
/// </summary>
public sealed class JsonMetadataWriter : IMetadataWriter
{
    private readonly MetadataOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private FileStream? _fileStream;
    private Utf8JsonWriter? _jsonWriter;
    private long _recordCount;
    private bool _disposed;

    public JsonMetadataWriter(MetadataOptions options)
    {
        _options = options;
        OutputPath = Path.Combine(options.OutputDirectory, options.JsonFileName);
    }

    public string Format => "json";
    public string OutputPath { get; }
    public long RecordCount => Interlocked.Read(ref _recordCount);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.OutputDirectory);

        _fileStream = new FileStream(
            OutputPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        _jsonWriter = new Utf8JsonWriter(_fileStream, new JsonWriterOptions
        {
            Indented = _options.JsonIndent,
            SkipValidation = false,
        });

        _jsonWriter.WriteStartObject();
        _jsonWriter.WriteString("schemaVersion", MetadataSchema.Version);
        _jsonWriter.WriteString("schemaId",      MetadataSchema.SchemaId);
        _jsonWriter.WriteString("generator",     MetadataSchema.GeneratorName);
        _jsonWriter.WritePropertyName("records");
        _jsonWriter.WriteStartArray();

        await _jsonWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendAsync(MetadataRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_jsonWriter is null) throw new InvalidOperationException("InitializeAsync was not called.");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _jsonWriter.WriteStartObject();
            _jsonWriter.WriteNumber("documentPartId",   record.DocumentPartId);
            _jsonWriter.WriteNumber("versionPart",      record.VersionPart);
            _jsonWriter.WriteString("title",            record.Title);
            _jsonWriter.WriteString("extension",        record.Extension);
            _jsonWriter.WriteNumber("logicalFileSize",  record.LogicalFileSize);
            _jsonWriter.WriteNumber("physicalFileSize", record.PhysicalFileSize);
            _jsonWriter.WriteString("lastWriteTime",    FormatDate(record.LastWriteTime));
            _jsonWriter.WriteString("exportPath",       record.ExportPath);
            _jsonWriter.WriteString("checksum",         record.Checksum);
            _jsonWriter.WriteString("exportStatus",     record.ExportStatus);
            _jsonWriter.WriteString("exportDate",       FormatDate(record.ExportDate));
            _jsonWriter.WriteNumber("workerId",         record.WorkerId);
            _jsonWriter.WriteNumber("retryCount",       record.RetryCount);

            if (_options.IncludeExtensionFields)
            {
                if (record.IdempotencyKey is not null)
                    _jsonWriter.WriteString("idempotencyKey", record.IdempotencyKey);
                else
                    _jsonWriter.WriteNull("idempotencyKey");

                if (record.DataFileVersionId is long dfv)
                    _jsonWriter.WriteNumber("dataFileVersionId", dfv);
                else
                    _jsonWriter.WriteNull("dataFileVersionId");
            }

            _jsonWriter.WriteEndObject();

            var count = Interlocked.Increment(ref _recordCount);
            if (_options.FlushEveryNRecords > 0 && count % _options.FlushEveryNRecords == 0)
            {
                await _jsonWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        if (_jsonWriter is null) return;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _jsonWriter.WriteEndArray();
            _jsonWriter.WriteEndObject();
            await _jsonWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_jsonWriter is not null)
        {
            try { await _jsonWriter.FlushAsync().ConfigureAwait(false); } catch { }
            await _jsonWriter.DisposeAsync().ConfigureAwait(false);
            _jsonWriter = null;
        }
        if (_fileStream is not null)
        {
            await _fileStream.DisposeAsync().ConfigureAwait(false);
            _fileStream = null;
        }
        _writeLock.Dispose();
    }

    private static string FormatDate(DateTime d) =>
        DateTime.SpecifyKind(d, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
