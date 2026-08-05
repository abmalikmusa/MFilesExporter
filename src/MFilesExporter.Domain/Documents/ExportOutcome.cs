namespace MFilesExporter.Domain.Documents;

public sealed record ExportOutcome
{
    public required IdempotencyKey IdempotencyKey { get; init; }
    public required DocumentFileVersionKey DocumentFileVersionKey { get; init; }
    public required DataFileVersionKey DataFileVersionKey { get; init; }
    public required ExportStatus Status { get; init; }
    public required long BytesWritten { get; init; }
    public string? OutputPath { get; init; }
    public string? Checksum { get; init; }
    public string? FailureReason { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public required int AttemptNumber { get; init; }
}
