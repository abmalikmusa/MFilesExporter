using System.Globalization;
using System.Text;
using MFilesExporter.Configuration.Options;

namespace MFilesExporter.Export.Metadata;

/// <summary>
/// Streaming CSV writer that produces <c>metadata.csv</c> per RFC 4180.
///
/// Format:
/// <list type="bullet">
///   <item><description>UTF-8 with optional BOM (default on — Excel needs it).</description></item>
///   <item><description>CRLF line endings.</description></item>
///   <item><description>Fields containing delimiter, quotes, or newlines are wrapped in double quotes; interior quotes are doubled.</description></item>
///   <item><description>Dates emitted in ISO 8601 UTC (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>) — portable to every mainstream database importer.</description></item>
/// </list>
/// </summary>
public sealed class CsvMetadataWriter : IMetadataWriter
{
    private static readonly string[] BaseHeader =
    {
        "DocumentPartId", "VersionPart", "Title", "Extension",
        "LogicalFileSize", "PhysicalFileSize", "LastWriteTime",
        "ExportPath", "Checksum", "ExportStatus", "ExportDate",
        "WorkerId", "RetryCount",
    };

    private static readonly string[] ExtensionHeader =
    {
        "IdempotencyKey", "DataFileVersionId",
    };

    private readonly MetadataOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private FileStream? _fileStream;
    private StreamWriter? _writer;
    private long _recordCount;
    private bool _disposed;

    public CsvMetadataWriter(MetadataOptions options)
    {
        _options = options;
        OutputPath = Path.Combine(options.OutputDirectory, options.CsvFileName);
    }

    public string Format => "csv";
    public string OutputPath { get; }
    public long RecordCount => Interlocked.Read(ref _recordCount);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.OutputDirectory);
        _fileStream = new FileStream(
            OutputPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        // Explicit UTF-8 (with/without BOM per config). Manual byte write for the
        // BOM lets us avoid StreamWriter's ambiguous default behavior.
        if (_options.CsvIncludeUtf8Bom)
        {
            byte[] bom = { 0xEF, 0xBB, 0xBF };
            await _fileStream.WriteAsync(bom, cancellationToken).ConfigureAwait(false);
        }

        _writer = new StreamWriter(_fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            NewLine = "\r\n",
        };

        // Schema-version comment (parsers should skip lines starting with '#').
        // Kept behind a config guard because strict CSV parsers may not skip.
        // Not emitted by default — the header row alone is the interoperable
        // signature.

        if (_options.CsvIncludeHeader)
        {
            var header = _options.IncludeExtensionFields
                ? string.Join(_options.CsvDelimiter, BaseHeader.Concat(ExtensionHeader))
                : string.Join(_options.CsvDelimiter, BaseHeader);
            await _writer.WriteLineAsync(header).ConfigureAwait(false);
        }
    }

    public async Task AppendAsync(MetadataRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_writer is null) throw new InvalidOperationException("InitializeAsync was not called.");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var line = ComposeLine(record);
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);

            var count = Interlocked.Increment(ref _recordCount);
            if (_options.FlushEveryNRecords > 0 && count % _options.FlushEveryNRecords == 0)
            {
                await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        if (_writer is not null)
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_writer is not null)
        {
            try { await _writer.FlushAsync().ConfigureAwait(false); } catch { /* ignore */ }
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }
        if (_fileStream is not null)
        {
            await _fileStream.DisposeAsync().ConfigureAwait(false);
            _fileStream = null;
        }
        _writeLock.Dispose();
    }

    private string ComposeLine(MetadataRecord r)
    {
        var sb = new StringBuilder(256);
        Append(sb, r.DocumentPartId.ToString(CultureInfo.InvariantCulture));
        AppendDelim(sb); Append(sb, r.VersionPart.ToString(CultureInfo.InvariantCulture));
        AppendDelim(sb); AppendEscaped(sb, r.Title);
        AppendDelim(sb); AppendEscaped(sb, r.Extension);
        AppendDelim(sb); Append(sb, r.LogicalFileSize.ToString(CultureInfo.InvariantCulture));
        AppendDelim(sb); Append(sb, r.PhysicalFileSize.ToString(CultureInfo.InvariantCulture));
        AppendDelim(sb); Append(sb, FormatDate(r.LastWriteTime));
        AppendDelim(sb); AppendEscaped(sb, r.ExportPath);
        AppendDelim(sb); AppendEscaped(sb, r.Checksum);
        AppendDelim(sb); AppendEscaped(sb, r.ExportStatus);
        AppendDelim(sb); Append(sb, FormatDate(r.ExportDate));
        AppendDelim(sb); Append(sb, r.WorkerId.ToString(CultureInfo.InvariantCulture));
        AppendDelim(sb); Append(sb, r.RetryCount.ToString(CultureInfo.InvariantCulture));

        if (_options.IncludeExtensionFields)
        {
            AppendDelim(sb); AppendEscaped(sb, r.IdempotencyKey ?? string.Empty);
            AppendDelim(sb); Append(sb, r.DataFileVersionId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string s) => sb.Append(s);

    private void AppendDelim(StringBuilder sb) => sb.Append(_options.CsvDelimiter);

    private void AppendEscaped(StringBuilder sb, string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return;
        }

        var needsQuote =
            s.Contains(_options.CsvDelimiter, StringComparison.Ordinal)
            || s.Contains('"')
            || s.Contains('\r')
            || s.Contains('\n');

        if (!needsQuote)
        {
            sb.Append(s);
            return;
        }

        sb.Append('"');
        foreach (var c in s)
        {
            if (c == '"') sb.Append('"'); // RFC 4180: doubled quote inside a quoted field
            sb.Append(c);
        }
        sb.Append('"');
    }

    private static string FormatDate(DateTime d) =>
        DateTime.SpecifyKind(d, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
