using System.Diagnostics;

namespace MFilesExporter.Export.Validation.Validators;

/// <summary>
/// Cheapest sanity check — does the target file exist on disk? Retryable
/// on failure because "temp file not yet renamed" is a legitimate
/// filesystem race the sink can recover from.
/// </summary>
public sealed class FileExistsValidator : IExportValidator
{
    public string Name => nameof(FileExistsValidator);
    public int Order => 0;

    public Task<ValidationCheckResult> ValidateAsync(
        ExportValidationContext context,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(context.OutputPath))
        {
            return Task.FromResult(ValidationCheckResult.Failed(
                Name, sw.Elapsed, "OutputPath is empty.", isRetryable: false));
        }
        if (!File.Exists(context.OutputPath))
        {
            return Task.FromResult(ValidationCheckResult.Failed(
                Name, sw.Elapsed, $"File not found at {context.OutputPath}.", isRetryable: true));
        }
        return Task.FromResult(ValidationCheckResult.Passed(Name, sw.Elapsed));
    }
}
