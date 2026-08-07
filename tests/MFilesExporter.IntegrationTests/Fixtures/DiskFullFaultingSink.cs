using System.Collections.Concurrent;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Domain.Documents;

namespace MFilesExporter.IntegrationTests.Fixtures;

/// <summary>
/// Test double that wraps a real <see cref="IDocumentSink"/> and injects a
/// transient <c>IOException("There is not enough space on the disk.")</c>
/// on the first write attempt of each document. Subsequent attempts are
/// forwarded to the inner sink and succeed.
/// </summary>
/// <remarks>
/// Exercises the retry pipeline end-to-end:
///   * <c>ExceptionClassifier</c> maps the message to <c>FailureCategory.DiskFull</c>.
///   * <c>RetryExecutor</c> applies the <c>DiskFull</c> category override
///     (cap 2 attempts, 1 s base delay).
///   * The second attempt succeeds because this sink only faults once
///     per document.
/// A pipeline that has retry wired correctly ends the run with 100%
/// Succeeded despite one injected fault per document.
/// </remarks>
public sealed class DiskFullFaultingSink : IDocumentSink
{
    private readonly IDocumentSink _inner;
    private readonly ConcurrentDictionary<string, int> _attemptsByKey = new(StringComparer.Ordinal);
    private int _injectedFaultCount;

    public DiskFullFaultingSink(IDocumentSink inner) => _inner = inner;

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
            throw new IOException("There is not enough space on the disk.");
        }

        return await _inner.WriteAsync(descriptor, content, cancellationToken).ConfigureAwait(false);
    }
}
