using FluentAssertions;
using MFilesExporter.Logging.Performance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MFilesExporter.Tests.Logging;

public class PerformanceLoggerTests
{
    [Fact]
    public async Task TimeAsync_Emits_On_Success()
    {
        var (perf, capture) = NewLogger();

        var result = await perf.TimeAsync("op-a", _ => ValueTask.FromResult(42), CancellationToken.None);

        result.Should().Be(42);
        capture.Entries.Should().ContainSingle().Which.Message.Should().Contain("outcome=success");
    }

    [Fact]
    public async Task TimeAsync_Emits_On_Failure_And_Rethrows()
    {
        var (perf, capture) = NewLogger();

        Func<Task> act = async () => await perf.TimeAsync<int>("op-b",
            _ => throw new InvalidOperationException("boom"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        capture.Entries.Should().ContainSingle();
        capture.Entries[0].Message.Should().Contain("outcome=failed");
        capture.Entries[0].Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void Begin_Scope_Attaches_Tags()
    {
        var (perf, capture) = NewLogger();
        using (var scope = perf.Begin("op-c"))
        {
            scope.SetTag("path", "/x").Complete(bytes: 1024);
        }

        capture.Entries.Should().ContainSingle();
        capture.Entries[0].Message.Should().Contain("outcome=success");
    }

    [Fact]
    public void Scope_Without_Complete_Reports_Unknown()
    {
        var (perf, capture) = NewLogger();
        using (perf.Begin("op-d")) { }

        capture.Entries[0].Message.Should().Contain("outcome=unknown");
    }

    private static (PerformanceLogger perf, CapturingLoggerProvider provider) NewLogger()
    {
        var provider = new CapturingLoggerProvider();
        var factory  = new LoggerFactory(new[] { provider });
        return (new PerformanceLogger(factory), provider);
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            public CapturingLogger(CapturingLoggerProvider owner) => _owner = owner;

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _owner.Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
