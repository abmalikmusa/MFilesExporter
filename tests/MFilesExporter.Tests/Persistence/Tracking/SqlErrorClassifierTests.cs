using FluentAssertions;
using MFilesExporter.Persistence.Tracking.Sql;

namespace MFilesExporter.Tests.Persistence.Tracking;

public class SqlErrorClassifierTests
{
    [Fact]
    public void OperationCanceledException_IsNotTransient()
    {
        SqlErrorClassifier.IsTransient(new OperationCanceledException()).Should().BeFalse();
    }

    [Fact]
    public void IOException_IsTransient()
    {
        SqlErrorClassifier.IsTransient(new IOException("io glitch")).Should().BeTrue();
    }

    [Fact]
    public void TimeoutException_IsTransient()
    {
        SqlErrorClassifier.IsTransient(new TimeoutException()).Should().BeTrue();
    }

    [Fact]
    public void InvalidOperationException_IsNotTransient()
    {
        SqlErrorClassifier.IsTransient(new InvalidOperationException()).Should().BeFalse();
    }

    [Fact]
    public void ArbitraryException_IsNotTransient()
    {
        SqlErrorClassifier.IsTransient(new NotSupportedException()).Should().BeFalse();
    }
}
