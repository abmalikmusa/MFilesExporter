using FluentAssertions;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.Dispatching;
using Microsoft.Extensions.DependencyInjection;

namespace MFilesExporter.Tests.Application;

public class ApplicationDispatcherTests
{
    private sealed record NoopCommand : ICommand;
    private sealed record IntCommand : ICommand<int>;
    private sealed record IntQuery : IQuery<int>;

    private sealed class NoopHandler : ICommandHandler<NoopCommand>
    {
        public int InvocationCount { get; private set; }
        public Task<ApplicationResult> HandleAsync(NoopCommand command, CancellationToken cancellationToken)
        {
            InvocationCount++;
            return Task.FromResult(ApplicationResult.Success());
        }
    }

    private sealed class IntHandler : ICommandHandler<IntCommand, int>
    {
        public Task<ApplicationResult<int>> HandleAsync(IntCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(ApplicationResult<int>.Success(7));
    }

    private sealed class IntQueryHandler : IQueryHandler<IntQuery, int>
    {
        public Task<ApplicationResult<int>> HandleAsync(IntQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(ApplicationResult<int>.Success(9));
    }

    [Fact]
    public async Task Send_ResolvesAndInvokesHandler()
    {
        var noop = new NoopHandler();
        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<NoopCommand>>(noop);
        var provider = services.BuildServiceProvider();

        var dispatcher = new ApplicationDispatcher(provider);
        var result = await dispatcher.SendAsync(new NoopCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        noop.InvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task SendGeneric_ReturnsPayload()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<IntCommand, int>, IntHandler>();
        var provider = services.BuildServiceProvider();

        var dispatcher = new ApplicationDispatcher(provider);
        var result = await dispatcher.SendAsync<IntCommand, int>(new IntCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(7);
    }

    [Fact]
    public async Task Query_ReturnsPayload()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IQueryHandler<IntQuery, int>, IntQueryHandler>();
        var provider = services.BuildServiceProvider();

        var dispatcher = new ApplicationDispatcher(provider);
        var result = await dispatcher.QueryAsync<IntQuery, int>(new IntQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(9);
    }

    [Fact]
    public async Task Send_MissingHandler_Throws()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();

        var dispatcher = new ApplicationDispatcher(provider);
        Func<Task> act = async () => await dispatcher.SendAsync(new NoopCommand(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
