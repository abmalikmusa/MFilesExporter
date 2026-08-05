using System.Diagnostics;

namespace MFilesExporter.Export.Validation.Validators;

/// <summary>
/// Ensures the output path lives beneath the configured root and that its
/// directory actually exists. Failures here indicate a configuration or
/// folder-strategy bug — deterministic, not retryable.
/// </summary>
public sealed class OutputFolderValidator : IExportValidator
{
    public string Name => nameof(OutputFolderValidator);
    public int Order => 10;

    public Task<ValidationCheckResult> ValidateAsync(
        ExportValidationContext context,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(context.ExpectedRootDirectory))
        {
            return Task.FromResult(ValidationCheckResult.Skipped(
                Name, sw.Elapsed, "ExpectedRootDirectory not supplied."));
        }

        var fullRoot = Path.GetFullPath(context.ExpectedRootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullOut = Path.GetFullPath(context.OutputPath);

        // Case-sensitivity policy: exact match on POSIX, case-insensitive on Windows.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var separatorSuffixed = fullRoot + Path.DirectorySeparatorChar;
        if (!fullOut.StartsWith(separatorSuffixed, comparison))
        {
            return Task.FromResult(ValidationCheckResult.Failed(
                Name, sw.Elapsed,
                $"OutputPath '{fullOut}' is not under ExpectedRootDirectory '{fullRoot}'.",
                isRetryable: false));
        }

        var directory = Path.GetDirectoryName(fullOut);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            return Task.FromResult(ValidationCheckResult.Failed(
                Name, sw.Elapsed,
                $"Parent directory '{directory}' does not exist.",
                isRetryable: true));
        }

        return Task.FromResult(ValidationCheckResult.Passed(Name, sw.Elapsed));
    }
}
