using System.Diagnostics;
using MFilesExporter.Configuration.Options;

namespace MFilesExporter.Export.Validation.Validators;

/// <summary>
/// Verifies the file's extension matches the expected one — case-insensitive.
/// When <see cref="ExportValidationOptions.AllowExtensionMismatch"/> is
/// true, downgrades a mismatch to a <see cref="ValidationCheckStatus.Warning"/>.
/// </summary>
public sealed class ExtensionValidator : IExportValidator
{
    private readonly ExportValidationOptions _options;

    public ExtensionValidator(ExportValidationOptions options)
    {
        _options = options;
    }

    public string Name => nameof(ExtensionValidator);
    public int Order => 20;

    public Task<ValidationCheckResult> ValidateAsync(
        ExportValidationContext context,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var actualExt = Path.GetExtension(context.OutputPath).TrimStart('.');
        var expectedExt = context.ExpectedExtension.TrimStart('.');

        if (string.Equals(actualExt, expectedExt, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ValidationCheckResult.Passed(Name, sw.Elapsed));
        }

        var reason = $"Expected extension '{expectedExt}', found '{actualExt}'.";
        return Task.FromResult(_options.AllowExtensionMismatch
            ? ValidationCheckResult.Warning(Name, sw.Elapsed, reason)
            : ValidationCheckResult.Failed(Name, sw.Elapsed, reason, isRetryable: false));
    }
}
