namespace MFilesExporter.Domain.WorkClaiming;

/// <summary>
/// Lifecycle of a single work item. Values are stable — never renumber.
/// </summary>
/// <remarks>
/// The state machine is deliberately narrow:
/// <code>
///   Available ──claim──► Claimed ──complete──► Completed (terminal)
///                            │
///                            ├──fail(transient, attempts &lt; max)──► Available
///                            ├──fail(transient, attempts ≥ max)──► DeadLettered (terminal)
///                            ├──fail(permanent)──► Failed (terminal)
///                            └──lease-expire──► Available
/// </code>
/// Nothing transitions out of Completed / Failed / DeadLettered. That is the
/// central invariant that makes the "no duplicate exports" guarantee formal.
/// </remarks>
public enum WorkItemStatus
{
    /// <summary>Not yet claimed; a call to <c>usp_ClaimWorkItems</c> may pick it up.</summary>
    Available    = 0,

    /// <summary>Owned by a worker with an active lease.</summary>
    Claimed      = 1,

    /// <summary>Successfully processed. Absorbing — never leaves this state.</summary>
    Completed    = 2,

    /// <summary>Permanently failed. Absorbing until an operator resets it.</summary>
    Failed       = 3,

    /// <summary>Exhausted retry budget. Absorbing; operator triage required.</summary>
    DeadLettered = 4,
}
