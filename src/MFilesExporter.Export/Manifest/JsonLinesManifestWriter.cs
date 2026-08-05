using System.Globalization;
using System.Text.Json;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Manifest;

/// <summary>
/// Append-only JSON-lines manifest with segment rotation and fsync-on-close.
/// Idempotent DisposeAsync.
/// </summary>
internal sealed class JsonLinesManifestWriter : IManifestWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly StorageOptions _options;
    private readonly ILogger<JsonLinesManifestWriter> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _runId;

    private FileStream? _current;
    private StreamWriter? _writer;
    private int _entriesInSegment;
    private int _segmentIndex;
    private bool _disposed;

    public JsonLinesManifestWriter(StorageOptions options, ILogger<JsonLinesManifestWriter> logger)
    {
        _options = options;
        _logger = logger;
        Directory.CreateDirectory(_options.ManifestPath);
        _runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
    }

    public async Task AppendAsync(ExportOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writer is null || _entriesInSegment >= _options.ManifestRotationEntryCount)
            {
                await RotateAsync().ConfigureAwait(false);
            }

            var record = new ManifestRecord
            {
                IdempotencyKey = outcome.IdempotencyKey.ToHex(),
                DocumentFilePartId = outcome.DocumentFileVersionKey.DocumentFilePartId,
                VersionPartId = outcome.DocumentFileVersionKey.VersionPartId,
                DataFileVersionId = outcome.DataFileVersionKey.DataFileVersionId,
                Status = outcome.Status.ToString(),
                BytesWritten = outcome.BytesWritten,
                OutputPath = outcome.OutputPath,
                Checksum = outcome.Checksum,
                FailureReason = outcome.FailureReason,
                ObservedAtUtc = outcome.ObservedAtUtc.UtcDateTime,
                AttemptNumber = outcome.AttemptNumber,
            };

            var line = JsonSerializer.Serialize(record, SerializerOptions);
            await _writer!.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            _entriesInSegment++;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writer is not null)
            {
                await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                _current?.Flush(flushToDisk: _options.FsyncManifestOnRotate);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RotateAsync()
    {
        await CloseCurrentAsync().ConfigureAwait(false);
        _segmentIndex++;
        var path = Path.Combine(_options.ManifestPath, $"manifest-{_runId}-{_segmentIndex:D6}.jsonl");
        _current = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _writer = new StreamWriter(_current, new System.Text.UTF8Encoding(false));
        _entriesInSegment = 0;
        _logger.LogInformation("Manifest segment opened: {Path}", path);
    }

    private async Task CloseCurrentAsync()
    {
        if (_writer is not null)
        {
            await _writer.FlushAsync().ConfigureAwait(false);
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }
        if (_current is not null)
        {
            try
            {
                if (_options.FsyncManifestOnRotate) _current.Flush(flushToDisk: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "fsync on manifest close failed");
            }
            await _current.DisposeAsync().ConfigureAwait(false);
            _current = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await CloseCurrentAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
        _lock.Dispose();
    }

    private sealed class ManifestRecord
    {
        public string IdempotencyKey { get; init; } = string.Empty;
        public long DocumentFilePartId { get; init; }
        public long VersionPartId { get; init; }
        public long DataFileVersionId { get; init; }
        public string Status { get; init; } = string.Empty;
        public long BytesWritten { get; init; }
        public string? OutputPath { get; init; }
        public string? Checksum { get; init; }
        public string? FailureReason { get; init; }
        public DateTime ObservedAtUtc { get; init; }
        public int AttemptNumber { get; init; }
    }
}
