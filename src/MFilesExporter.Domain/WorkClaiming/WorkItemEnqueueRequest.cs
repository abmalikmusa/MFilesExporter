using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Domain.WorkClaiming;

/// <summary>
/// Producer-side record for a single work item to enqueue. The claim engine
/// treats these as immutable inputs; downstream consumers see only
/// <see cref="ClaimedWorkItem"/>.
/// </summary>
public sealed record WorkItemEnqueueRequest
{
    public required DocumentFileVersionKey DocumentFileVersionKey { get; init; }
    public required DataFileVersionKey DataFileVersionKey { get; init; }
    public required IdempotencyKey IdempotencyKey { get; init; }
    public int Priority { get; init; }
    public int MaxAttempts { get; init; } = 5;
}
