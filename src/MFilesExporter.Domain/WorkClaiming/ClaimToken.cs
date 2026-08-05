namespace MFilesExporter.Domain.WorkClaiming;

/// <summary>
/// Per-claim fencing token — a fresh GUID stamped on the row by
/// <c>usp_ClaimWorkItems</c>. Every subsequent state transition (complete,
/// fail, renew) must present this token to succeed. If the worker's lease
/// expired and the reaper cleared the token, the worker's next call fails
/// safely — the row will never be Completed by two different claimants.
/// </summary>
public readonly record struct ClaimToken(Guid Value)
{
    public static ClaimToken None { get; } = new(Guid.Empty);
    public bool IsAssigned => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}
