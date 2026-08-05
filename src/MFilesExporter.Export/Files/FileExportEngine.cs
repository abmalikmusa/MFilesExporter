using System.Buffers;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Export.Files.Naming;
using MFilesExporter.Export.Files.Strategies;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Files;

/// <summary>
/// Default <see cref="IFileExportEngine"/>.
///
/// Write algorithm:
/// <list type="number">
///   <item><description>Sanitize TITLE + EXTENSION → safe filename.</description></item>
///   <item><description>Compose relative folder from <see cref="IFolderStrategy"/>.</description></item>
///   <item><description>Resolve duplicates via <see cref="IDuplicateResolver"/>.</description></item>
///   <item><description>Apply Windows long-path prefix if necessary.</description></item>
///   <item><description>Write to a temp file in the target directory.</description></item>
///   <item><description>fsync (optional) then atomic rename to the final path.</description></item>
/// </list>
/// Temp-then-rename means a crash mid-write never leaves a partial file
/// visible at the final path.
/// </summary>
public sealed class FileExportEngine : IFileExportEngine
{
    private readonly FileExportOptions _options;
    private readonly IFolderStrategy _folderStrategy;
    private readonly IFilenameSanitizer _sanitizer;
    private readonly IDuplicateResolver _duplicateResolver;
    private readonly ILogger<FileExportEngine> _logger;

    public FileExportEngine(
        FileExportOptions options,
        IFolderStrategy folderStrategy,
        IFilenameSanitizer sanitizer,
        IDuplicateResolver duplicateResolver,
        ILogger<FileExportEngine> logger)
    {
        _options = options;
        _folderStrategy = folderStrategy;
        _sanitizer = sanitizer;
        _duplicateResolver = duplicateResolver;
        _logger = logger;
    }

    public async Task<FileExportResult> ExportAsync(
        FileExportContext context,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(content);

        // 1. Sanitize filename.
        var descriptor = context.Descriptor;
        var filename = _sanitizer.Sanitize(descriptor.Title, descriptor.Extension, out var titleSanitized);

        // 2. Relative folder from strategy.
        var relativeFolder = _folderStrategy.BuildRelativeFolder(context);
        var directory = string.IsNullOrEmpty(relativeFolder)
            ? _options.RootPath
            : Path.Combine(_options.RootPath, relativeFolder);

        // 3. Desired full path.
        var desiredPath = Path.Combine(directory, filename);

        // 4. Long-path guard — if the desired path is too long, downgrade
        //    to a hash-based short name in the same directory.
        var (finalPath, longPathPrefixed) = ApplyLongPathHandling(desiredPath, descriptor.IdempotencyKey.ToHex());

        // 5. Duplicate resolution.
        var resolvedPath = _duplicateResolver.Resolve(finalPath, descriptor);
        var disambiguated = !string.Equals(resolvedPath, finalPath, StringComparison.Ordinal);

        Directory.CreateDirectory(Path.GetDirectoryName(resolvedPath)!);

        // 6. Temp-then-rename. Temp lives in the same directory to keep
        //    File.Move atomic (same volume).
        var tempPath = Path.Combine(
            Path.GetDirectoryName(resolvedPath)!,
            $".{Path.GetFileName(resolvedPath)}.{descriptor.IdempotencyKey.ToHex()[..8]}.partial");

        long bytesWritten = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(_options.WriteBufferSize);
        FileStream? fs = null;
        try
        {
            var openMode = _duplicateResolver.Kind == DuplicateResolutionKind.Overwrite
                ? FileMode.Create
                : FileMode.CreateNew;

            fs = new FileStream(
                tempPath, openMode, FileAccess.Write, FileShare.None,
                _options.WriteBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            int read;
            while ((read = await content.ReadAsync(
                        buffer.AsMemory(0, _options.WriteBufferSize),
                        cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytesWritten += read;
            }

            await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (_options.FsyncOnWrite)
            {
                fs.Flush(flushToDisk: true);
            }
        }
        catch
        {
            try
            {
                if (fs is not null) { await fs.DisposeAsync().ConfigureAwait(false); fs = null; }
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Failed to clean up temp file {Path}", tempPath);
            }
            throw;
        }
        finally
        {
            if (fs is not null) await fs.DisposeAsync().ConfigureAwait(false);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // 7. Atomic rename.
        File.Move(tempPath, resolvedPath, overwrite: _options.OverwriteOnCollision);

        _logger.LogDebug(
            "Exported document {IdempotencyKey} to {Path} ({Bytes} bytes; sanitized={Sanitized}; disambiguated={Disambiguated})",
            descriptor.IdempotencyKey, resolvedPath, bytesWritten, titleSanitized, disambiguated);

        return new FileExportResult
        {
            OutputPath                  = resolvedPath,
            OutputDirectory             = Path.GetDirectoryName(resolvedPath)!,
            FinalFilename               = Path.GetFileName(resolvedPath),
            BytesWritten                = bytesWritten,
            DisambiguatedFromDuplicate  = disambiguated,
            TitleWasSanitized           = titleSanitized,
            RequiredLongPathPrefix      = longPathPrefixed,
        };
    }

    /// <summary>
    /// Long-path policy:
    /// <list type="bullet">
    ///   <item><description>If the full path is under <see cref="FileExportOptions.MaxFullPathLength"/>, no change.</description></item>
    ///   <item><description>Otherwise: replace the filename with a short hash-based one in the same directory.
    ///     This preserves the folder strategy while guaranteeing writeability on Windows.</description></item>
    /// </list>
    /// On Windows the <c>\\?\</c> prefix would also work but is only meaningful for absolute paths;
    /// the short-name fallback works everywhere and never changes the behaviour of downstream consumers.
    /// </summary>
    private (string path, bool prefixed) ApplyLongPathHandling(string desiredPath, string idempotencyHex)
    {
        if (desiredPath.Length <= _options.MaxFullPathLength)
        {
            return (desiredPath, false);
        }

        var directory = Path.GetDirectoryName(desiredPath)!;
        var extension = Path.GetExtension(desiredPath);
        var shortName = idempotencyHex[..16] + extension;
        var shortPath = Path.Combine(directory, shortName);

        _logger.LogWarning(
            "Path {Path} exceeded {Max} chars — falling back to short hash-based name {Short}",
            desiredPath, _options.MaxFullPathLength, shortName);

        return (shortPath, true);
    }
}
