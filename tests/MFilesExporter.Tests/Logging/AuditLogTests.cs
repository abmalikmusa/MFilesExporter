using FluentAssertions;
using MFilesExporter.Logging.Audit;
using MFilesExporter.Logging.Correlation;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Tests.Logging;

public class AuditLogTests
{
    [Fact]
    public async Task WriteAsync_Emits_Info_Line_With_All_Fields()
    {
        var (audit, capture) = NewAudit();
        await audit.WriteAsync("document.exported", "worker-3", "DFV/42", "success",
            new Dictionary<string, object?> { ["Bytes"] = 1024 });

        capture.Entries.Should().ContainSingle();
        var line = capture.Entries[0].Message;
        line.Should().Contain("action=document.exported");
        line.Should().Contain("actor=worker-3");
        line.Should().Contain("subject=DFV/42");
        line.Should().Contain("outcome=success");
        line.Should().Contain("category=Audit");
    }

    [Fact]
    public async Task Correlation_Comes_From_Ambient_When_Missing()
    {
        var accessor = new CorrelationIdAccessor();
        var (audit, capture) = NewAudit(accessor);

        using (accessor.Push("test-cid"))
        {
            await audit.WriteAsync("job.started", "system", "job/1", "success");
        }

        capture.Entries[0].Message.Should().Contain("correlationId=test-cid");
    }

    [Fact]
    public async Task Provided_Correlation_Overrides_Ambient()
    {
        var accessor = new CorrelationIdAccessor();
        var (audit, capture) = NewAudit(accessor);

        using (accessor.Push("ambient"))
        {
            await audit.WriteAsync(new AuditEvent
            {
                TimestampUtc  = DateTimeOffset.UtcNow,
                Action        = "job.completed",
                Actor         = "system",
                Subject       = "job/1",
                Outcome       = "success",
                CorrelationId = "explicit",
            });
        }

        capture.Entries[0].Message.Should().Contain("correlationId=explicit");
    }

    private static (AuditLog audit, CapturingLoggerProvider provider) NewAudit(ICorrelationIdAccessor? accessor = null)
    {
        var provider = new CapturingLoggerProvider();
        var factory  = new LoggerFactory(new[] { provider });
        return (new AuditLog(factory, accessor ?? new CorrelationIdAccessor()), provider);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

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
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                Func<TState, Exception?, string> formatter)
                => _owner.Entries.Add(new LogEntry(level, formatter(state, ex)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
