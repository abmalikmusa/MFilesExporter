using FluentAssertions;
using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Tests.Domain;

public class IdempotencyKeyTests
{
    [Fact]
    public void For_IsDeterministic()
    {
        var a = IdempotencyKey.For(10, 3, 99);
        var b = IdempotencyKey.For(10, 3, 99);
        a.Should().Be(b);
    }

    [Fact]
    public void For_DifferentInputsProduceDifferentHashes()
    {
        IdempotencyKey.For(10, 3, 99).Should().NotBe(IdempotencyKey.For(10, 4, 99));
    }

    [Fact]
    public void Parse_RoundTripsHex()
    {
        var original = IdempotencyKey.For(1, 2, 3);
        IdempotencyKey.Parse(original.ToHex()).Should().Be(original);
    }
}
