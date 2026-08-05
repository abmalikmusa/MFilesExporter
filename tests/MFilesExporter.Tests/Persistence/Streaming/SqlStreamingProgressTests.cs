using FluentAssertions;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Persistence.MFiles.Streaming;

namespace MFilesExporter.Tests.Persistence.Streaming;

public class SqlStreamingProgressTests
{
    [Fact]
    public void RowsPerSecond_HandlesZeroElapsed()
    {
        var p = new SqlStreamingProgress
        {
            RowsYielded    = 1000,
            PagesFetched   = 1,
            RetryAttempts  = 0,
            LastCursor     = DocumentFileVersionKey.Origin,
            ObservedAtUtc  = DateTimeOffset.UtcNow,
            Elapsed        = TimeSpan.Zero,
        };
        p.RowsPerSecond.Should().Be(0);
    }

    [Fact]
    public void RowsPerSecond_ComputesCorrectly()
    {
        var p = new SqlStreamingProgress
        {
            RowsYielded    = 500,
            PagesFetched   = 5,
            RetryAttempts  = 0,
            LastCursor     = new DocumentFileVersionKey(10, 20),
            ObservedAtUtc  = DateTimeOffset.UtcNow,
            Elapsed        = TimeSpan.FromSeconds(5),
        };
        p.RowsPerSecond.Should().Be(100);
    }
}
