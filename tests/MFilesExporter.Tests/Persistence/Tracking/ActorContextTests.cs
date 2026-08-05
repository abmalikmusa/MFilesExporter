using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Persistence.Tracking.Sql;

namespace MFilesExporter.Tests.Persistence.Tracking;

public class ActorContextTests
{
    [Fact]
    public void Resolve_PrefersOverride()
    {
        var opts = new TrackingDatabaseOptions { ActorNameOverride = "svc-exporter-01" };
        ActorContext.Resolve(opts).Should().Be("svc-exporter-01");
    }

    [Fact]
    public void Resolve_FallsBackToMachineName()
    {
        var opts = new TrackingDatabaseOptions { ActorNameOverride = null };
        ActorContext.Resolve(opts).Should().Be(Environment.MachineName);
    }

    [Fact]
    public void Resolve_IgnoresWhitespaceOverride()
    {
        var opts = new TrackingDatabaseOptions { ActorNameOverride = "   " };
        ActorContext.Resolve(opts).Should().Be(Environment.MachineName);
    }
}
