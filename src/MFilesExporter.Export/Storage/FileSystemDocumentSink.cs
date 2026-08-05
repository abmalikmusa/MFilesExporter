using System.Buffers;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Storage;

/// <summary>
/// Atomic filesystem sink: writes to a temp sibling, fsyncs, then renames.
/// Never leaves a truncated file at the final path.
/// </summary>
internal sealed class FileSystemDocumentSink : IDocumentSink
{
    private readonly PathBuilder _pathBuilder;
    private readonly IChecksumCalculatorFactory _checksumFactory;
    private readonly StorageOptions _options;
    private readonly ILogger<FileSystemDocumentSink> _logger;

    public FileSystemDocumentSink(
        PathBuilder pathBuilder,
        IChecksumCalculatorFactory checksumFactory,
        StorageOptions options,
        ILogger<FileSystemDocumentSink> logger)
    {
        _pathBuilder = pathBuilder;
        _checksumFactory = checksumFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<DocumentSinkResult> WriteAsync(
        DocumentDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken)
    {
        var finalPath = _pathBuilder.BuildOutputPath(descriptor);
        var tempPath = _pathBuilder.BuildTempPath(finalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        long bytesWritten = 0;
        using var checksum = _checksumFactory.Create();
        var buffer = ArrayPool<byte>.Shared.Rent(_options.WriteBufferSize);
        FileStream? file = null;

        try
        {
            file = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                _options.WriteBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, _options.WriteBufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                checksum.Append(buffer.AsSpan(0, read));
                bytesWritten += read;
            }

            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
        }
        catch
        {
            try
            {
                if (file is not null) { await file.DisposeAsync().ConfigureAwait(false); file = null; }
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Failed to clean up temp {Path}", tempPath);
            }
            throw;
        }
        finally
        {
            if (file is not null) await file.DisposeAsync().ConfigureAwait(false);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        File.Move(tempPath, finalPath, overwrite: true);
        var hash = checksum.FinalizeHex();

        _logger.LogDebug("Wrote {Bytes} to {Path} sha={Sha}", bytesWritten, finalPath, hash);
        return new DocumentSinkResult(finalPath, bytesWritten, hash);
    }
}
