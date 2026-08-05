namespace MFilesExporter.Export.Validation;

/// <summary>
/// Runs every registered <see cref="IExportValidator"/> for a single
/// exported document and returns an aggregated report. Callers use
/// <see cref="ExportValidationReport.IsValid"/> and
/// <see cref="ExportValidationReport.AllFailuresRetryable"/> to decide
/// between Complete / retry-transient / fail-permanent.
/// </summary>
public interface IExportValidationPipeline
{
    Task<ExportValidationReport> ValidateAsync(
        ExportValidationContext context,
        CancellationToken cancellationToken);
}
