using System.Collections.Concurrent;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Domain.Documents;

namespace MFilesExporter.IntegrationTests.Fixtures;

/// <summary>
/// Wraps a real <see cref="IDocumentSink"/> and injects an operator-supplied
/// exception on the first write attempt of each document. Subsequent attempts
/// forward to the inner sink and succeed.
/// </summary>
/// <remarks>
/// Exercises every retryable classifier branch end-to-end. Callers pass in
/// the exception factory matching the category they want to test:
/// <list type="bullet">
///   <item><description><c>DiskFull</c> — <c>IOException("not enough space")</c></description></item>
///   <item><description><c>SqlTimeout</c> — <c>TimeoutException</c></description></item>
///   <item><description><c>NetworkInterruption</c> — <c>SocketException(ConnectionReset)</c></description></item>
/// </list>
/// </remarks>
public sealed class FirstAttemptFaultingSink : IDocumentSink
{
    private readonly IDocumentSink _inner;
    private readonly Func<Exception> _exceptionFactory;
    private readonly ConcurrentDictionary<string, int> _attemptsByKey = new(StringComparer.Ordinal);
    private int _injectedFaultCount;

    public FirstAttemptFaultingSink(IDocumentSink inner, Func<Exception> exceptionFactory)
    {
        _inner = inner;
        _exceptionFactory = exceptionFactory;
    }

    /// <summary>Total number of injected fault throws across the run.</summary>
    public int InjectedFaultCount => Volatile.Read(ref _injectedFaultCount);

    public async Task<DocumentSinkResult> WriteAsync(
        DocumentDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken)
    {
        var key = descriptor.IdempotencyKey.ToHex();
        var attemptNumber = _attemptsByKey.AddOrUpdate(key, 1, (_, n) => n + 1);

        if (attemptNumber == 1)
        {
            Interlocked.Increment(ref _injectedFaultCount);
            throw _exceptionFactory();
        }

        return await _inner.WriteAsync(descriptor, content, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Back-compat alias — the original DiskFull-specific fault sink used by
/// the first fault-injection test. Delegates to
/// <see cref="FirstAttemptFaultingSink"/>.
/// </summary>
public sealed class DiskFullFaultingSink : IDocumentSink
{
    private readonly FirstAttemptFaultingSink _inner;

    public DiskFullFaultingSink(IDocumentSink inner) =>
        _inner = new FirstAttemptFaultingSink(
            inner,
            () => new IOException("There is not enough space on the disk."));

    public int InjectedFaultCount => _inner.InjectedFaultCount;

    public Task<DocumentSinkResult> WriteAsync(
        DocumentDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken)
        => _inner.WriteAsync(descriptor, content, cancellationToken);
}
