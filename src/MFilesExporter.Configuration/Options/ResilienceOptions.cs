namespace MFilesExporter.Configuration.Options;

public sealed class ResilienceOptions
{
    public const string SectionName = "Exporter:Resilience";

    public ResiliencePolicySettings SqlRead { get; set; } = new()
    {
        MaxRetryAttempts = 5, BaseDelayMilliseconds = 500, MaxDelaySeconds = 30, TimeoutSeconds = 300,
        CircuitBreakerFailureRatio = 0.5, CircuitBreakerMinimumThroughput = 20,
        CircuitBreakerSamplingSeconds = 30, CircuitBreakerBreakSeconds = 60,
    };
    public ResiliencePolicySettings SqlBlobRead { get; set; } = new()
    {
        MaxRetryAttempts = 5, BaseDelayMilliseconds = 500, MaxDelaySeconds = 30, TimeoutSeconds = 600,
        CircuitBreakerFailureRatio = 0.5, CircuitBreakerMinimumThroughput = 20,
        CircuitBreakerSamplingSeconds = 30, CircuitBreakerBreakSeconds = 60,
    };
    public ResiliencePolicySettings DiskWrite { get; set; } = new()
    {
        MaxRetryAttempts = 3, BaseDelayMilliseconds = 250, MaxDelaySeconds = 15, TimeoutSeconds = 300,
        CircuitBreakerFailureRatio = 0.5, CircuitBreakerMinimumThroughput = 20,
        CircuitBreakerSamplingSeconds = 30, CircuitBreakerBreakSeconds = 30,
    };
    public ResiliencePolicySettings StateStore { get; set; } = new()
    {
        MaxRetryAttempts = 5, BaseDelayMilliseconds = 100, MaxDelaySeconds = 5, TimeoutSeconds = 30,
        CircuitBreakerFailureRatio = 0.5, CircuitBreakerMinimumThroughput = 50,
        CircuitBreakerSamplingSeconds = 30, CircuitBreakerBreakSeconds = 15,
    };
}

public sealed class ResiliencePolicySettings
{
    public int MaxRetryAttempts { get; set; }
    public int BaseDelayMilliseconds { get; set; }
    public int MaxDelaySeconds { get; set; }
    public int TimeoutSeconds { get; set; }
    public double CircuitBreakerFailureRatio { get; set; }
    public int CircuitBreakerMinimumThroughput { get; set; }
    public int CircuitBreakerSamplingSeconds { get; set; }
    public int CircuitBreakerBreakSeconds { get; set; }
}
