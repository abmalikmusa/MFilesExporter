using FluentAssertions;
using MFilesExporter.Export.Parallel;

namespace MFilesExporter.Tests.Export.Parallel;

public class AsyncManualResetEventTests
{
    [Fact]
    public async Task InitiallySet_WaitReturnsImmediately()
    {
        var evt = new AsyncManualResetEvent(initiallySet: true);
        await evt.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        evt.IsSet.Should().BeTrue();
    }

    [Fact]
    public async Task ResetBlocksWaiters_SetReleasesThem()
    {
        var evt = new AsyncManualResetEvent(initiallySet: false);
        var completions = 0;

        var tasks = Enumerable.Range(0, 5).Select(async _ =>
        {
            await evt.WaitAsync();
            Interlocked.Increment(ref completions);
        }).ToArray();

        await Task.Delay(50);
        Volatile.Read(ref completions).Should().Be(0, "reset must block waiters");

        evt.Set();
        await Task.WhenAll(tasks);
        Volatile.Read(ref completions).Should().Be(5);
    }

    [Fact]
    public async Task Reset_AfterSet_BlocksNewWaiters()
    {
        var evt = new AsyncManualResetEvent(initiallySet: true);
        await evt.WaitAsync();       // immediate

        evt.Reset();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Func<Task> act = async () => await evt.WaitAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Set_Then_Reset_Then_Set_ReleasesFreshWaiters()
    {
        var evt = new AsyncManualResetEvent(initiallySet: true);
        evt.Reset();

        var completed = 0;
        var waiter = Task.Run(async () =>
        {
            await evt.WaitAsync();
            Interlocked.Increment(ref completed);
        });

        await Task.Delay(50);
        Volatile.Read(ref completed).Should().Be(0);

        evt.Set();
        await waiter;
        Volatile.Read(ref completed).Should().Be(1);
    }

    [Fact]
    public void Reset_IsIdempotent_WhenAlreadyReset()
    {
        var evt = new AsyncManualResetEvent(initiallySet: false);
        evt.Reset();
        evt.Reset();
        evt.IsSet.Should().BeFalse();
    }
}
