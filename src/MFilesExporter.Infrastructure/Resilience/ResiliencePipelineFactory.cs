using System.Collections.Concurrent;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Configuration.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace MFilesExporter.Infrastructure.Resilience;

internal sealed class ResiliencePipelineFactory : IResiliencePipelineProvider
{
    private readonly ResilienceOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, ResiliencePipeline> _cache = new(StringComparer.Ordinal);

    public ResiliencePipelineFactory(ResilienceOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public ValueTask<T> ExecuteAsync<T>(string pipelineName, Func<CancellationToken, ValueTask<T>> op, CancellationToken cancellationToken) =>
        _cache.GetOrAdd(pipelineName, BuildPipeline).ExecuteAsync(op, cancellationToken);

    public ValueTask ExecuteAsync(string pipelineName, Func<CancellationToken, ValueTask> op, CancellationToken cancellationToken) =>
        _cache.GetOrAdd(pipelineName, BuildPipeline).ExecuteAsync(op, cancellationToken);

    private ResiliencePipeline BuildPipeline(string name)
    {
        var s = ResolveSettings(name);
        var logger = _loggerFactory.CreateLogger("Resilience." + name);

        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = s.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(s.BaseDelayMilliseconds),
                MaxDelay = TimeSpan.FromSeconds(s.MaxDelaySeconds),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception, "[{P}] retry {Attempt}/{Max} after {Delay}",
                        name, args.AttemptNumber, s.MaxRetryAttempts, args.RetryDelay);
                    return ValueTask.CompletedTask;
                },
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = s.CircuitBreakerFailureRatio,
                MinimumThroughput = s.CircuitBreakerMinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(s.CircuitBreakerSamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(s.CircuitBreakerBreakSeconds),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
                OnOpened = args =>
                {
                    logger.LogError(args.Outcome.Exception, "[{P}] circuit OPEN for {Duration}", name, args.BreakDuration);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("[{P}] circuit CLOSED", name);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    logger.LogInformation("[{P}] circuit HALF-OPEN", name);
                    return ValueTask.CompletedTask;
                },
            })
            .AddTimeout(TimeSpan.FromSeconds(s.TimeoutSeconds))
            .Build();
    }

    private ResiliencePolicySettings ResolveSettings(string name) => name switch
    {
        ResiliencePipelineNames.SqlRead => _options.SqlRead,
        ResiliencePipelineNames.SqlBlobRead => _options.SqlBlobRead,
        ResiliencePipelineNames.DiskWrite => _options.DiskWrite,
        ResiliencePipelineNames.StateStore => _options.StateStore,
        _ => throw new ArgumentException($"Unknown resilience pipeline: {name}", nameof(name)),
    };

    private static bool IsTransient(Exception ex) => ex switch
    {
        OperationCanceledException => false,
        SqlException sqlEx => IsTransientSqlError(sqlEx.Number),
        IOException => true,
        TimeoutException => true,
        _ => false,
    };

    private static bool IsTransientSqlError(int number) => number switch
    {
        1205 or 1222 => true,
        -2 or 10053 or 10054 or 10060 => true,
        40197 or 40501 or 40613 or 49918 or 49919 or 49920 => true,
        _ => false,
    };
}
