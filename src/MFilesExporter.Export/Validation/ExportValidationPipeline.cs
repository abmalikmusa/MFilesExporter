using System.Diagnostics;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Export.Validation.Reporting;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Validation;

/// <summary>
/// Default <see cref="IExportValidationPipeline"/>. Runs validators in
/// ascending <c>Order</c>, stopping on the first failure under
/// <see cref="ValidationExecutionMode.FailFast"/> or continuing under
/// <see cref="ValidationExecutionMode.RunAll"/>.
/// </summary>
/// <remarks>
/// Retry integration:
/// <list type="bullet">
///   <item><description>If every failure carries <c>IsRetryable = true</c>, the report's
///     <see cref="ExportValidationReport.AllFailuresRetryable"/> is true — callers should
///     surface a transient failure so the work-claim engine reclaims the item.</description></item>
///   <item><description>Any non-retryable failure is deterministic — callers should
///     record a permanent failure so the item does not spin in a retry loop.</description></item>
/// </list>
/// </remarks>
public sealed class ExportValidationPipeline : IExportValidationPipeline
{
    private readonly IReadOnlyList<IExportValidator> _validators;
    private readonly IReadOnlyList<IValidationReporter> _reporters;
    private readonly ExportValidationOptions _options;
    private readonly ILogger<ExportValidationPipeline> _logger;

    public ExportValidationPipeline(
        IEnumerable<IExportValidator> validators,
        IEnumerable<IValidationReporter> reporters,
        ExportValidationOptions options,
        ILogger<ExportValidationPipeline> logger)
    {
        _validators = validators.OrderBy(v => v.Order).ToArray();
        _reporters = reporters.ToArray();
        _options = options;
        _logger = logger;
    }

    public async Task<ExportValidationReport> ValidateAsync(
        ExportValidationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var overall = Stopwatch.StartNew();
        var results = new List<ValidationCheckResult>(_validators.Count);

        if (!_options.Enabled)
        {
            overall.Stop();
            return new ExportValidationReport
            {
                Checks       = results,
                TotalElapsed = overall.Elapsed,
            };
        }

        var applicable = _options.EnabledValidators.Count == 0
            ? _validators
            : _validators.Where(v => _options.EnabledValidators.Contains(v.Name)).ToArray();

        foreach (var validator in applicable)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.PerValidatorTimeout);

            var perValidator = Stopwatch.StartNew();
            ValidationCheckResult result;

            try
            {
                result = await validator.ValidateAsync(context, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                result = ValidationCheckResult.Failed(
                    validator.Name, perValidator.Elapsed,
                    $"Validator exceeded {_options.PerValidatorTimeout} timeout.",
                    isRetryable: true);
            }
            catch (OperationCanceledException)
            {
                throw;   // real cancellation, propagate
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Validator {Validator} threw for {Path}",
                    validator.Name, context.OutputPath);
                result = ValidationCheckResult.Failed(
                    validator.Name, perValidator.Elapsed,
                    $"Unexpected exception: {ex.GetType().Name}: {ex.Message}",
                    isRetryable: false);
            }

            results.Add(result);

            if (result.Status == ValidationCheckStatus.Failed
                && _options.Mode == ValidationExecutionMode.FailFast)
            {
                break;
            }
        }

        overall.Stop();
        var report = new ExportValidationReport
        {
            Checks       = results,
            TotalElapsed = overall.Elapsed,
        };

        // Fan-out to reporters. Faulty reporters must never poison the caller.
        foreach (var reporter in _reporters)
        {
            try
            {
                await reporter.ReportAsync(context, report, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Validation reporter {Reporter} threw; continuing.",
                    reporter.GetType().Name);
            }
        }

        return report;
    }
}
