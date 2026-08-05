using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Persistence.MFiles.Blobs;

/// <summary>
/// Default <see cref="IBinaryObjectReader"/>. Bounded, streaming, checksummable.
///
/// Loop:
/// <code>
///   position = 0
///   while (bytesRead := reader.GetBytes(ordinal, position, buffer, 0, bufferSize)) &gt; 0:
///     hash.Append(buffer[..bytesRead])
///     destination.Write(buffer[..bytesRead])
///     position += bytesRead
/// </code>
/// The buffer is rented from <see cref="ArrayPool{Byte}.Shared"/> so no
/// long-lived allocation grows with BLOB size.
/// </summary>
public sealed class BinaryObjectReader : IBinaryObjectReader
{
    private readonly ILogger<BinaryObjectReader>? _logger;

    public BinaryObjectReader(ILogger<BinaryObjectReader>? logger = null)
    {
        _logger = logger;
    }

    public Task<BinaryReadResult> ReadAsync(
        SqlDataReader reader,
        int ordinal,
        Stream destination,
        BinaryReadOptions options,
        IProgress<BinaryReadProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Validate call-site invariants first (options + ordinal). These are
        // cheap-to-check, caller-supplied constants; catching them before the
        // runtime state checks gives the caller a specific failure signal.
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BufferSize, 4_096);
        if (options.Checksum == BinaryChecksumAlgorithm.None && !string.IsNullOrEmpty(options.ExpectedChecksumHex))
        {
            throw new ArgumentException(
                "ExpectedChecksumHex was set but Checksum is None — this can never match.",
                nameof(options));
        }

        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        }

        return ReadInternalAsync(
            (offset, buf, bufOffset, len) => reader.GetBytes(ordinal, offset, buf, bufOffset, len),
            destination, options, progress, cancellationToken);
    }

    /// <summary>
    /// Delegate signature that mirrors
    /// <see cref="SqlDataReader.GetBytes(int, long, byte[], int, int)"/> so the
    /// copy loop can be unit-tested against an in-memory source without
    /// requiring a live SQL Server.
    /// </summary>
    public delegate long GetBytesDelegate(long fieldOffset, byte[] buffer, int bufferOffset, int length);

    /// <summary>
    /// Testable copy loop. Not part of the public interface but exposed
    /// as <c>internal</c> so unit tests in <c>MFilesExporter.Tests</c> can
    /// drive it directly (see <c>InternalsVisibleTo</c> in
    /// <c>Directory.Build.props</c>).
    /// </summary>
    internal static async Task<BinaryReadResult> ReadInternalAsync(
        GetBytesDelegate getBytes,
        Stream destination,
        BinaryReadOptions options,
        IProgress<BinaryReadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var bufferSize = options.BufferSize;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        IncrementalHash? hasher = options.Checksum == BinaryChecksumAlgorithm.None
            ? null
            : IncrementalHash.CreateHash(HashNameFor(options.Checksum));

        long position = 0;
        long chunkCount = 0;
        var sw = Stopwatch.StartNew();
        var lastProgress = TimeSpan.Zero;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // GetBytes returns 0 at EOF. Uses long field offset — supports > 4 GiB.
                long bytesRead = getBytes(position, buffer, 0, bufferSize);
                if (bytesRead <= 0)
                {
                    break;
                }

                var chunk = (int)bytesRead; // safe: chunk ≤ bufferSize ≤ int.MaxValue
                hasher?.AppendData(buffer, 0, chunk);
                await destination.WriteAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);

                position += bytesRead;
                chunkCount++;

                if (progress is not null
                    && options.ProgressReportInterval > TimeSpan.Zero
                    && sw.Elapsed - lastProgress >= options.ProgressReportInterval)
                {
                    ReportProgress(progress, position, options.ExpectedByteCount, chunkCount, sw.Elapsed);
                    lastProgress = sw.Elapsed;
                }
            }

            sw.Stop();

            string? checksumHex = null;
            if (hasher is not null)
            {
                var hashBytes = hasher.GetHashAndReset();
                checksumHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            // Final progress tick guarantees observers see 100 %.
            if (progress is not null)
            {
                ReportProgress(progress, position, options.ExpectedByteCount, chunkCount, sw.Elapsed);
            }

            // Validation.
            BinaryReadValidation? validation = null;
            if (options.ExpectedByteCount.HasValue || options.ExpectedChecksumHex is not null)
            {
                var sizeOk = !options.ExpectedByteCount.HasValue
                             || position == options.ExpectedByteCount.Value;

                var checksumOk = options.ExpectedChecksumHex is null
                                 || string.Equals(checksumHex, options.ExpectedChecksumHex, StringComparison.OrdinalIgnoreCase);

                validation = new BinaryReadValidation(
                    ByteCountMatches:    sizeOk,
                    ChecksumMatches:     checksumOk,
                    ExpectedByteCount:   options.ExpectedByteCount,
                    ExpectedChecksumHex: options.ExpectedChecksumHex);

                if (!validation.IsValid && options.ThrowOnValidationFailure)
                {
                    var reason = !sizeOk && !checksumOk
                        ? $"Byte-count mismatch ({position} vs {options.ExpectedByteCount}) AND checksum mismatch ({checksumHex} vs {options.ExpectedChecksumHex})"
                        : !sizeOk
                            ? $"Byte-count mismatch: expected {options.ExpectedByteCount}, actual {position}"
                            : $"Checksum mismatch: expected {options.ExpectedChecksumHex}, actual {checksumHex}";
                    throw new BinaryValidationException(reason, validation);
                }
            }

            return new BinaryReadResult
            {
                BytesRead         = position,
                ChunkCount        = chunkCount,
                Elapsed           = sw.Elapsed,
                ChecksumHex       = checksumHex,
                ChecksumAlgorithm = options.Checksum,
                Validation        = validation,
            };
        }
        finally
        {
            hasher?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReportProgress(
        IProgress<BinaryReadProgress> progress,
        long bytesTransferred,
        long? expected,
        long chunks,
        TimeSpan elapsed)
    {
        try
        {
            progress.Report(new BinaryReadProgress(
                BytesTransferred:   bytesTransferred,
                ExpectedByteCount:  expected,
                ChunksRead:         chunks,
                Elapsed:            elapsed));
        }
        catch
        {
            // A progress consumer must never fault the read.
        }
    }

    private static HashAlgorithmName HashNameFor(BinaryChecksumAlgorithm alg) => alg switch
    {
        BinaryChecksumAlgorithm.Sha256 => HashAlgorithmName.SHA256,
        BinaryChecksumAlgorithm.Sha1   => HashAlgorithmName.SHA1,
        BinaryChecksumAlgorithm.Sha512 => HashAlgorithmName.SHA512,
        BinaryChecksumAlgorithm.Md5    => HashAlgorithmName.MD5,
        _ => throw new ArgumentOutOfRangeException(nameof(alg), alg, "Unsupported checksum algorithm."),
    };
}
