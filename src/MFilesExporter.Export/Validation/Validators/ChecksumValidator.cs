using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using MFilesExporter.Configuration.Options;

namespace MFilesExporter.Export.Validation.Validators;

/// <summary>
/// Re-computes the SHA-256 of the written file and compares it to the
/// expected value emitted by the sink. This is the strongest correctness
/// check and the most expensive — sorted last so cheaper validators can
/// short-circuit under FailFast.
///
/// A mismatch is deterministic — the file on disk does not agree with
/// what the sink says it wrote. Never retry blindly; the caller should
/// treat this as data corruption and re-export from source.
/// </summary>
public sealed class ChecksumValidator : IExportValidator
{
    private const int BufferSize = 81_920;

    private readonly ExportValidationOptions _options;

    public ChecksumValidator(ExportValidationOptions options)
    {
        _options = options;
    }

    public string Name => nameof(ChecksumValidator);
    public int Order => 50;

    public async Task<ValidationCheckResult> ValidateAsync(
        ExportValidationContext context,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (!_options.RerunChecksumFromFile)
        {
            return ValidationCheckResult.Skipped(
                Name, sw.Elapsed, "RerunChecksumFromFile disabled.");
        }
        if (string.IsNullOrWhiteSpace(context.ExpectedChecksumHex))
        {
            return ValidationCheckResult.Skipped(
                Name, sw.Elapsed, "No expected checksum supplied.");
        }
        if (!File.Exists(context.OutputPath))
        {
            return ValidationCheckResult.Failed(
                Name, sw.Elapsed, "File missing.", isRetryable: true);
        }

        string actualHex;
        try
        {
            actualHex = await ComputeSha256Async(context.OutputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return ValidationCheckResult.Failed(
                Name, sw.Elapsed, $"IO error while hashing: {ex.Message}", isRetryable: true);
        }

        if (!string.Equals(actualHex, context.ExpectedChecksumHex, StringComparison.OrdinalIgnoreCase))
        {
            return ValidationCheckResult.Failed(
                Name, sw.Elapsed,
                $"Checksum mismatch: expected {context.ExpectedChecksumHex}, actual {actualHex}.",
                isRetryable: false);
        }
        return ValidationCheckResult.Passed(Name, sw.Elapsed);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;
            while ((read = await fs.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
