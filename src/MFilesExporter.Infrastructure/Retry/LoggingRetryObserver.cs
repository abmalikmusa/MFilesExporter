using MFilesExporter.Application.Abstractions.Retry;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Infrastructure.Retry;

/// <summary>
/// Structured-logging observer. Emits one INFO record per attempt with the
/// operation name, attempt number, failure category, delay, and correlation id.
/// </summary>
public sealed class LoggingRetryObserver : IRetryObserver
{
    private readonly ILogger<LoggingRetryObserver> _logger;

    public LoggingRetryObserver(ILogger<LoggingRetryObserver> logger) => _logger = logger;

    public ValueTask OnRetryAsync(RetryAttemptContext attempt, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "retry.attempt op={Operation} attempt={Attempt}/{Max} category={Category} delay={Delay} correlationId={CorrelationId}",
            attempt.OperationName, attempt.AttemptNumber, attempt.MaxAttempts,
            attempt.Category, attempt.Delay, attempt.CorrelationId);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnOutcomeAsync(RetryOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome.Succeeded)
        {
            _logger.LogInformation(
                "retry.outcome op={Operation} status=succeeded attempts={Attempts} elapsed={Elapsed} correlationId={CorrelationId}",
                outcome.OperationName, outcome.TotalAttempts, outcome.TotalElapsed, outcome.CorrelationId);
        }
        else
        {
            _logger.LogError(outcome.FinalException,
                "retry.outcome op={Operation} status=failed attempts={Attempts} elapsed={Elapsed} category={Category} correlationId={CorrelationId}",
                outcome.OperationName, outcome.TotalAttempts, outcome.TotalElapsed, outcome.FinalCategory, outcome.CorrelationId);
        }

        return ValueTask.CompletedTask;
    }
}
