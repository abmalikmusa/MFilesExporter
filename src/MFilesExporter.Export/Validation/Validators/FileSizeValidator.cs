using System.Diagnostics;

namespace MFilesExporter.Export.Validation.Validators;

/// <summary>
/// Compares <c>FileInfo.Length</c> to the expected byte count. A mismatch
/// is deterministic — the file is either short (write was truncated) or
/// long (write ran past EOF, either bug or double-write). Neither
/// condition improves with retry.
/// </summary>
public sealed class FileSizeValidator : IExportValidator
{
    public string Name => nameof(FileSizeValidator);
    public int Order => 30;

    public Task<ValidationCheckResult> ValidateAsync(
        ExportValidationContext context,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (!File.Exists(context.OutputPath))
        {
            // Should have been caught upstream; treat as retryable in case the
            // FS is racing on rename.
            return Task.FromResult(ValidationCheckResult.Failed(
                Name, sw.Elapsed, "File does not exist to inspect.", isRetryable: true));
        }

        var actual = new FileInfo(context.OutputPath).Length;
        if (actual != context.ExpectedByteCount)
        {
            return Task.FromResult(ValidationCheckResult.Failed(
                Name, sw.Elapsed,
                $"Size mismatch: expected {context.ExpectedByteCount}, actual {actual}.",
                isRetryable: false));
        }
        return Task.FromResult(ValidationCheckResult.Passed(Name, sw.Elapsed));
    }
}
