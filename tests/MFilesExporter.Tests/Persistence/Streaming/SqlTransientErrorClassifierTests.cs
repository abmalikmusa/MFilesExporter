using System.Reflection;
using FluentAssertions;

namespace MFilesExporter.Tests.Persistence.Streaming;

/// <summary>
/// The streaming engine ships a self-contained transient-error classifier
/// (independent of the tracking-DB one) so its retry semantics can evolve
/// separately. This test locks the classification decisions.
/// </summary>
public class SqlTransientErrorClassifierTests
{
    private static readonly Type ClassifierType =
        Type.GetType("MFilesExporter.Persistence.MFiles.Streaming.SqlTransientErrorClassifier, MFilesExporter.Persistence")!;

    private static bool IsTransient(Exception ex)
    {
        var m = ClassifierType.GetMethod("IsTransient", BindingFlags.Public | BindingFlags.Static)!;
        return (bool)m.Invoke(null, new object[] { ex })!;
    }

    [Fact]
    public void OperationCanceledException_IsNotTransient() =>
        IsTransient(new OperationCanceledException()).Should().BeFalse();

    [Fact]
    public void TimeoutException_IsTransient() =>
        IsTransient(new TimeoutException()).Should().BeTrue();

    [Fact]
    public void IOException_IsTransient() =>
        IsTransient(new IOException("net glitch")).Should().BeTrue();

    [Fact]
    public void InvalidOperationException_IsNotTransient() =>
        IsTransient(new InvalidOperationException()).Should().BeFalse();
}
