namespace MFilesExporter.Application.Abstractions.Dashboard;

/// <summary>
/// Optional source for the "total expected" number displayed on the dashboard.
/// Typically implemented by a component with authoritative visibility of the
/// source enumeration count (job planner, checkpoint engine, ...).
/// </summary>
/// <remarks>
/// If no implementation is registered the dashboard shows the target as
/// <c>?</c> and computes throughput but not ETA.
/// </remarks>
public interface ITotalExpectedSource
{
    /// <summary>Total documents scheduled for the current run. <c>0</c> = unknown.</summary>
    long TotalExpected { get; }
}

/// <summary>Optional running total of retry attempts observed across the process.</summary>
public interface IRetryCounterSource
{
    /// <summary>Monotonic count of retries observed since process start.</summary>
    long TotalRetries { get; }
}
