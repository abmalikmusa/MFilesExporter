using MFilesExporter.Persistence.MFiles;
using Microsoft.Data.SqlClient;

namespace MFilesExporter.IntegrationTests.Fixtures;

/// <summary>
/// Decorates a real <see cref="ISqlConnectionFactory"/> and throws an
/// operator-supplied exception on the first <paramref name="faultsToInject"/>
/// calls to <see cref="OpenAsync"/>. Subsequent calls forward to the inner
/// factory and succeed. Used to prove the retry executor recovers from
/// transient source-side SQL faults.
/// </summary>
public sealed class FirstNOpensFaultingConnectionFactory : ISqlConnectionFactory
{
    private readonly ISqlConnectionFactory _inner;
    private readonly Func<Exception> _exceptionFactory;
    private int _remainingFaults;
    private int _injectedFaultCount;

    public FirstNOpensFaultingConnectionFactory(
        ISqlConnectionFactory inner,
        int faultsToInject,
        Func<Exception> exceptionFactory)
    {
        _inner = inner;
        _remainingFaults = faultsToInject;
        _exceptionFactory = exceptionFactory;
    }

    public int InjectedFaultCount => Volatile.Read(ref _injectedFaultCount);

    public Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Decrement(ref _remainingFaults) >= 0)
        {
            Interlocked.Increment(ref _injectedFaultCount);
            throw _exceptionFactory();
        }
        return _inner.OpenAsync(cancellationToken);
    }
}
