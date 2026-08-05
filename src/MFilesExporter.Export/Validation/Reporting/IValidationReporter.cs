namespace MFilesExporter.Export.Validation.Reporting;

/// <summary>
/// Sink for validation reports. Multiple reporters can be registered — the
/// pipeline fans out to every one after the report is composed. Reporters
/// MUST NOT throw; a faulty reporter should not fault the export.
/// </summary>
public interface IValidationReporter
{
    Task ReportAsync(
        ExportValidationContext context,
        ExportValidationReport report,
        CancellationToken cancellationToken);
}
