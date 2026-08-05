using System.Reflection;
using FluentAssertions;
using MFilesExporter.Persistence.Tracking.Sql;

namespace MFilesExporter.Tests.Persistence.Tracking;

/// <summary>
/// The exponential backoff is a private static — we test it directly via
/// reflection so we can pin the schedule without depending on SQL Server.
/// </summary>
public class SqlExecutorRetryTests
{
    [Fact]
    public void Backoff_IsExponentialWithinJitterBand()
    {
        var method = typeof(SqlExecutor).GetMethod(
            "ComputeBackoff",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var baseSchedule = new[] { 250, 500, 1000, 2000, 4000 };

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var actual = (TimeSpan)method!.Invoke(null, new object[] { attempt })!;
            var expectedBase = baseSchedule[attempt - 1];
            actual.TotalMilliseconds.Should().BeInRange(expectedBase * 0.7, expectedBase * 1.3,
                "attempt {0} backoff should be near {1}ms ±25%", attempt, expectedBase);
        }
    }
}
