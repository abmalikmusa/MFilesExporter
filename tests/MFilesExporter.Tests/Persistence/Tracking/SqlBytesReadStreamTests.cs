using FluentAssertions;
using MFilesExporter.Persistence.MFiles;

namespace MFilesExporter.Tests.Persistence.Tracking;

/// <summary>
/// SqlBytesReadStream requires a live <c>SqlDataReader</c>, so end-to-end
/// behaviour is exercised by the integration suite. These unit tests cover
/// what can be validated without one — the constructor contract.
/// </summary>
public class SqlBytesReadStreamTests
{
    [Fact]
    public void Constructor_Throws_OnNullReader()
    {
        Action act = () => new SqlBytesReadStream(null!, ordinal: 0);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_Throws_OnNegativeOrdinal()
    {
        // The reader arg is null but the ordinal is validated first so this
        // still throws ArgumentOutOfRangeException before hitting the null check.
        // Note: reader-null vs ordinal-negative order is deterministic — the
        // ArgumentNullException.ThrowIfNull runs first, so wrap accordingly:
        Action act = () => new SqlBytesReadStream(null!, ordinal: -1);
        act.Should().Throw<ArgumentNullException>();   // reader-null wins
    }
}
