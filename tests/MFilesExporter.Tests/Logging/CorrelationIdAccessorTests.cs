using FluentAssertions;
using MFilesExporter.Logging.Correlation;

namespace MFilesExporter.Tests.Logging;

public class CorrelationIdAccessorTests
{
    [Fact]
    public void Current_Is_Null_Outside_Scope()
    {
        var accessor = new CorrelationIdAccessor();
        accessor.Current.Should().BeNull();
    }

    [Fact]
    public void Push_Sets_And_Dispose_Restores()
    {
        var accessor = new CorrelationIdAccessor();
        using (accessor.Push("abc"))
        {
            accessor.Current.Should().Be("abc");
        }
        accessor.Current.Should().BeNull();
    }

    [Fact]
    public void Nested_Push_Restores_Outer_On_Dispose()
    {
        var accessor = new CorrelationIdAccessor();
        using (accessor.Push("outer"))
        {
            accessor.Current.Should().Be("outer");
            using (accessor.Push("inner"))
            {
                accessor.Current.Should().Be("inner");
            }
            accessor.Current.Should().Be("outer");
        }
        accessor.Current.Should().BeNull();
    }

    [Fact]
    public async Task Value_Flows_Across_Async_Boundaries()
    {
        var accessor = new CorrelationIdAccessor();
        using (accessor.Push("flow-id"))
        {
            await Task.Yield();
            await Task.Delay(1);
            accessor.Current.Should().Be("flow-id");
        }
    }

    [Fact]
    public async Task Sibling_Async_Flows_Are_Isolated()
    {
        var accessor = new CorrelationIdAccessor();

        async Task<string?> Branch(string id)
        {
            using (accessor.Push(id))
            {
                await Task.Delay(10);
                return accessor.Current;
            }
        }

        var results = await Task.WhenAll(Branch("A"), Branch("B"), Branch("C"));
        results.Should().BeEquivalentTo(new[] { "A", "B", "C" });
        accessor.Current.Should().BeNull();
    }

    [Fact]
    public void PushNew_Yields_A_Fresh_Id()
    {
        var accessor = new CorrelationIdAccessor();
        using (accessor.PushNew(out var id1))
        using (accessor.PushNew(out var id2))
        {
            id1.Should().NotBeNullOrEmpty();
            id2.Should().NotBeNullOrEmpty();
            id1.Should().NotBe(id2);
        }
    }

    [Fact]
    public void NewId_Returns_32_Hex_Characters()
    {
        var id = new CorrelationIdAccessor().NewId();
        id.Should().HaveLength(32);
        id.Should().MatchRegex("^[0-9a-f]{32}$");
    }
}
