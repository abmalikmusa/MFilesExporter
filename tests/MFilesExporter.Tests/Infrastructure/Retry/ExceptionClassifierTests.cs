using System.ComponentModel;
using System.Net.Sockets;
using System.Reflection;
using System.Security;
using FluentAssertions;
using MFilesExporter.Application.Abstractions.Retry;
using MFilesExporter.Infrastructure.Retry;
using Microsoft.Data.SqlClient;

namespace MFilesExporter.Tests.Infrastructure.Retry;

public class ExceptionClassifierTests
{
    private readonly ExceptionClassifier _classifier = new();

    [Fact]
    public void Cancellation_Beats_Everything()
    {
        _classifier.Classify(new OperationCanceledException()).Should().Be(FailureCategory.Cancelled);
    }

    [Theory]
    [InlineData(1205, FailureCategory.SqlDeadlock)]
    [InlineData(1222, FailureCategory.SqlDeadlock)]
    [InlineData(-2,   FailureCategory.SqlTimeout)]
    [InlineData(10053, FailureCategory.NetworkInterruption)]
    [InlineData(10054, FailureCategory.NetworkInterruption)]
    [InlineData(40501, FailureCategory.RateLimited)]
    [InlineData(49918, FailureCategory.RateLimited)]
    [InlineData(18456, FailureCategory.PermissionDenied)]
    public void Sql_Numbers_Map_To_Categories(int sqlErrorNumber, FailureCategory expected)
    {
        var sql = MakeSqlException(sqlErrorNumber);
        _classifier.Classify(sql).Should().Be(expected);
    }

    [Fact]
    public void TimeoutException_Is_SqlTimeout()
    {
        _classifier.Classify(new TimeoutException()).Should().Be(FailureCategory.SqlTimeout);
    }

    [Fact]
    public void UnauthorizedAccess_Is_Permission()
    {
        _classifier.Classify(new UnauthorizedAccessException()).Should().Be(FailureCategory.PermissionDenied);
    }

    [Fact]
    public void SecurityException_Is_Permission()
    {
        _classifier.Classify(new SecurityException("denied")).Should().Be(FailureCategory.PermissionDenied);
    }

    [Fact]
    public void DiskFull_Detected_By_Message()
    {
        var io = new IOException("There is not enough space on the disk.");
        _classifier.Classify(io).Should().Be(FailureCategory.DiskFull);
    }

    [Fact]
    public void Generic_IO_Is_IoFailure()
    {
        _classifier.Classify(new IOException("some read failure")).Should().Be(FailureCategory.IoFailure);
    }

    [Fact]
    public void Socket_Errors_Are_Network()
    {
        _classifier.Classify(new SocketException((int)SocketError.ConnectionReset))
            .Should().Be(FailureCategory.NetworkInterruption);
        _classifier.Classify(new SocketException((int)SocketError.HostUnreachable))
            .Should().Be(FailureCategory.NetworkInterruption);
    }

    [Fact]
    public void Socket_Timeout_Is_Timeout()
    {
        _classifier.Classify(new SocketException((int)SocketError.TimedOut)).Should().Be(FailureCategory.SqlTimeout);
    }

    [Fact]
    public void Win32DiskFull_Is_DiskFull()
    {
        _classifier.Classify(new Win32Exception(112)).Should().Be(FailureCategory.DiskFull);
    }

    [Fact]
    public void ArgumentException_Is_Permanent()
    {
        _classifier.Classify(new ArgumentException("bad arg")).Should().Be(FailureCategory.Permanent);
    }

    [Fact]
    public void UnknownException_Falls_Through()
    {
        _classifier.Classify(new Exception("mystery")).Should().Be(FailureCategory.Unknown);
    }

    [Fact]
    public void Aggregate_Unwraps_To_Inner()
    {
        var inner = new SocketException((int)SocketError.ConnectionReset);
        var agg = new AggregateException(inner);
        _classifier.Classify(agg).Should().Be(FailureCategory.NetworkInterruption);
    }

    // SqlException has no public constructors; fabricate one via reflection helpers.
    private static SqlException MakeSqlException(int number)
    {
        var errorCtor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(c => c.GetParameters().Length >= 7);
        var parms = errorCtor.GetParameters();

        object?[] args = new object?[parms.Length];
        for (int i = 0; i < parms.Length; i++)
        {
            args[i] = parms[i].ParameterType switch
            {
                { } t when t == typeof(int)    => 0,
                { } t when t == typeof(byte)   => (byte)0,
                { } t when t == typeof(string) => string.Empty,
                { } t when t == typeof(Exception) => null,
                { } t when t == typeof(uint)   => (uint)0,
                _ => null,
            };
        }
        // Parameter 0 is number, parameter 1 is state, parameter 2 is class, etc.
        args[0] = number;
        var error = (SqlError)errorCtor.Invoke(args);

        var collectionCtor = typeof(SqlErrorCollection).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!;
        var collection = (SqlErrorCollection)collectionCtor.Invoke(null);
        var addMethod = typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!;
        addMethod.Invoke(collection, new object?[] { error });

        var exceptionCtor = typeof(SqlException).GetMethod("CreateException",
            BindingFlags.NonPublic | BindingFlags.Static,
            new[] { typeof(SqlErrorCollection), typeof(string) });

        if (exceptionCtor is not null)
            return (SqlException)exceptionCtor.Invoke(null, new object?[] { collection, "server" })!;

        // Fallback for signatures with extra parameters.
        var candidate = typeof(SqlException).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(m => m.Name == "CreateException");
        var candidateParms = candidate.GetParameters();
        object?[] cArgs = new object?[candidateParms.Length];
        for (int i = 0; i < candidateParms.Length; i++)
        {
            cArgs[i] = candidateParms[i].ParameterType switch
            {
                { } t when t == typeof(SqlErrorCollection) => collection,
                { } t when t == typeof(string) => "server",
                { } t when t == typeof(Guid)   => Guid.Empty,
                { } t when t == typeof(Exception) => null,
                _ => null,
            };
        }
        return (SqlException)candidate.Invoke(null, cArgs)!;
    }
}
