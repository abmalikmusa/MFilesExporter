using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Validation.Reporting;

/// <summary>
/// Default reporter — emits a structured log line per report and one per
/// failed / warning check. Uses information-level for success, warning
/// for retryable failures, error for deterministic failures.
/// </summary>
public sealed class LoggingValidationReporter : IValidationReporter
{
    private readonly ILogger<LoggingValidationReporter> _logger;

    public LoggingValidationReporter(ILogger<LoggingValidationReporter> logger)
    {
        _logger = logger;
    }

    public Task ReportAsync(
        ExportValidationContext context,
        ExportValidationReport report,
        CancellationToken cancellationToken)
    {
        if (report.IsValid && !report.HasWarnings)
        {
            _logger.LogDebug(
                "Validation passed for {OutputPath} — {Summary}",
                context.OutputPath, report.ToSummaryLine());
        }
        else if (report.HasFailures && report.AllFailuresRetryable)
        {
            _logger.LogWarning(
                "Validation FAILED (retryable) for {OutputPath} — {Summary}",
                context.OutputPath, report.ToSummaryLine());
            foreach (var check in report.Failures)
            {
                _logger.LogWarning(
                    "  ↳ [{Validator}] {Reason}",
                    check.ValidatorName, check.FailureReason);
            }
        }
        else if (report.HasFailures)
        {
            _logger.LogError(
                "Validation FAILED (permanent) for {OutputPath} — {Summary}",
                context.OutputPath, report.ToSummaryLine());
            foreach (var check in report.Failures)
            {
                _logger.LogError(
                    "  ↳ [{Validator}] {Reason}",
                    check.ValidatorName, check.FailureReason);
            }
        }
        else if (report.HasWarnings)
        {
            _logger.LogInformation(
                "Validation passed with warnings for {OutputPath} — {Summary}",
                context.OutputPath, report.ToSummaryLine());
        }

        return Task.CompletedTask;
    }
}
