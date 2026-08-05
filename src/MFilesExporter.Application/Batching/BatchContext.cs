using MFilesExporter.Application.Common;

namespace MFilesExporter.Application.Batching;

/// <summary>
/// Ambient context passed through every batch operation. Immutable per run;
/// individual items derive their own scoped logging from
/// <see cref="CorrelationId"/>.
/// </summary>
public sealed record BatchContext
{
    /// <summary>Owning job — used by the source and processors to scope work.</summary>
    public required long ExportJobId { get; init; }

    /// <summary>Worker holding the run — used to attribute claims + audit.</summary>
    public required long WorkerId { get; init; }

    /// <summary>Partition scope for the run.</summary>
    public required string PartitionKey { get; init; }

    /// <summary>Correlation ID shared by every log line for this run.</summary>
    public required CorrelationId CorrelationId { get; init; }
}
