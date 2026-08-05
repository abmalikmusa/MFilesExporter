using MFilesExporter.Application.Abstractions.Monitoring;

namespace MFilesExporter.Infrastructure.Monitoring;

/// <summary>
/// Pure ETA math extracted for testability. Given the elapsed time and
/// documents completed, projects the remaining seconds using a simple
/// linear-rate model.
/// </summary>
/// <remarks>
/// Returns <c>null</c> when the calculation is not defined (job not started,
/// no expected total, or zero documents completed). The gauge treats
/// <c>null</c> as "no measurement" and emits nothing, so the ETA panel in
/// Grafana simply shows an empty series rather than a misleading zero.
/// </remarks>
public static class EtaCalculator
{
    public static double? EstimateSeconds(IProgressSnapshotProvider progress)
        => EstimateSeconds(
            progress.TotalRecorded,
            progress.TotalExpected,
            progress.StartedAtUtc,
            DateTimeOffset.UtcNow);

    public static double? EstimateSeconds(long totalRecorded, long totalExpected, DateTimeOffset? startedAt, DateTimeOffset now)
    {
        if (startedAt is null || totalExpected <= 0 || totalRecorded <= 0) return null;
        if (totalRecorded >= totalExpected) return 0;

        var elapsed = (now - startedAt.Value).TotalSeconds;
        if (elapsed <= 0) return null;

        var ratePerSecond = totalRecorded / elapsed;
        if (ratePerSecond <= 0) return null;

        var remaining = totalExpected - totalRecorded;
        return remaining / ratePerSecond;
    }
}
