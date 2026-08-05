using System.Diagnostics;
using MFilesExporter.Configuration.Options;

namespace MFilesExporter.Export.Validation.Validators;

/// <summary>
/// Cross-references the emitted metadata record against the actual export.
/// Every mismatch here indicates an internal bug — the metadata catalog
/// and the on-disk file disagree — so failures are non-retryable and
/// require developer investigation.
/// </summary>
public sealed class MetadataConsistencyValidator : IExportValidator
{
    private readonly ExportValidationOptions _options;

    public MetadataConsistencyValidator(ExportValidationOptions options)
    {
        _options = options;
    }

    public string Name => nameof(MetadataConsistencyValidator);
    public int Order => 60;

    public Task<ValidationCheckResult> ValidateAsync(
        ExportValidationContext context,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (!_options.ValidateMetadataConsistency)
        {
            return Task.FromResult(ValidationCheckResult.Skipped(
                Name, sw.Elapsed, "Metadata-consistency check disabled."));
        }

        var record = context.MetadataRecord;
        if (record is null)
        {
            return Task.FromResult(ValidationCheckResult.Skipped(
                Name, sw.Elapsed, "No metadata record supplied."));
        }

        var mismatches = new List<string>(4);

        if (!string.Equals(record.ExportPath, context.OutputPath, StringComparison.Ordinal))
        {
            mismatches.Add($"ExportPath (record='{record.ExportPath}', actual='{context.OutputPath}')");
        }

        if (record.LogicalFileSize != context.ExpectedByteCount)
        {
            mismatches.Add($"LogicalFileSize (record={record.LogicalFileSize}, expected={context.ExpectedByteCount})");
        }

        if (!string.IsNullOrEmpty(context.ExpectedChecksumHex)
            && !string.Equals(record.Checksum, context.ExpectedChecksumHex, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add($"Checksum (record='{record.Checksum}', expected='{context.ExpectedChecksumHex}')");
        }

        var expectedExt = context.ExpectedExtension.TrimStart('.');
        var recordExt = record.Extension.TrimStart('.');
        if (!string.IsNullOrEmpty(expectedExt)
            && !string.Equals(recordExt, expectedExt, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add($"Extension (record='{record.Extension}', expected='{context.ExpectedExtension}')");
        }

        if (mismatches.Count == 0)
        {
            return Task.FromResult(ValidationCheckResult.Passed(Name, sw.Elapsed));
        }

        return Task.FromResult(ValidationCheckResult.Failed(
            Name, sw.Elapsed,
            "Metadata inconsistent with export: " + string.Join("; ", mismatches),
            isRetryable: false));
    }
}
