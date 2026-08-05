using System.Diagnostics;
using MFilesExporter.Application.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Application.Dispatching;

/// <summary>
/// Reflection-free dispatcher — every call resolves the concrete handler
/// type by generic argument. No dynamic proxy, no Emit, no scanning.
/// Behavior pipelines are composed by wrapping this class with a
/// <see cref="LoggingApplicationDispatcher"/> decorator in DI.
/// </summary>
public sealed class ApplicationDispatcher : IApplicationDispatcher
{
    private readonly IServiceProvider _services;

    public ApplicationDispatcher(IServiceProvider services)
    {
        _services = services;
    }

    public Task<ApplicationResult> SendAsync<TCommand>(
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(command);
        var handler = _services.GetRequiredService<ICommandHandler<TCommand>>();
        return handler.HandleAsync(command, cancellationToken);
    }

    public Task<ApplicationResult<TResult>> SendAsync<TCommand, TResult>(
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : ICommand<TResult>
    {
        ArgumentNullException.ThrowIfNull(command);
        var handler = _services.GetRequiredService<ICommandHandler<TCommand, TResult>>();
        return handler.HandleAsync(command, cancellationToken);
    }

    public Task<ApplicationResult<TResult>> QueryAsync<TQuery, TResult>(
        TQuery query,
        CancellationToken cancellationToken)
        where TQuery : IQuery<TResult>
    {
        ArgumentNullException.ThrowIfNull(query);
        var handler = _services.GetRequiredService<IQueryHandler<TQuery, TResult>>();
        return handler.HandleAsync(query, cancellationToken);
    }
}

/// <summary>
/// Decorator that logs every dispatch with a correlation ID and timing.
/// Wraps <see cref="ApplicationDispatcher"/> so consumers see one interface.
/// </summary>
public sealed class LoggingApplicationDispatcher : IApplicationDispatcher
{
    private readonly IApplicationDispatcher _inner;
    private readonly ILogger<LoggingApplicationDispatcher> _logger;

    public LoggingApplicationDispatcher(
        IApplicationDispatcher inner,
        ILogger<LoggingApplicationDispatcher> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public async Task<ApplicationResult> SendAsync<TCommand>(
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : ICommand
    {
        var correlation = CorrelationId.New();
        var name = typeof(TCommand).Name;
        using var scope = _logger.BeginScope("cid={CorrelationId} cmd={Command}", correlation, name);

        _logger.LogInformation("Dispatch begin: {Command}", name);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.SendAsync(command, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            LogOutcome(name, result.IsSuccess, sw.Elapsed, result.PrimaryError);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Dispatch faulted: {Command} in {Elapsed}", name, sw.Elapsed);
            throw;
        }
    }

    public async Task<ApplicationResult<TResult>> SendAsync<TCommand, TResult>(
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : ICommand<TResult>
    {
        var correlation = CorrelationId.New();
        var name = typeof(TCommand).Name;
        using var scope = _logger.BeginScope("cid={CorrelationId} cmd={Command}", correlation, name);

        _logger.LogInformation("Dispatch begin: {Command}", name);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.SendAsync<TCommand, TResult>(command, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            LogOutcome(name, result.IsSuccess, sw.Elapsed, result.PrimaryError);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Dispatch faulted: {Command} in {Elapsed}", name, sw.Elapsed);
            throw;
        }
    }

    public async Task<ApplicationResult<TResult>> QueryAsync<TQuery, TResult>(
        TQuery query,
        CancellationToken cancellationToken)
        where TQuery : IQuery<TResult>
    {
        var correlation = CorrelationId.New();
        var name = typeof(TQuery).Name;
        using var scope = _logger.BeginScope("cid={CorrelationId} qry={Query}", correlation, name);

        _logger.LogDebug("Query begin: {Query}", name);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.QueryAsync<TQuery, TResult>(query, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            LogOutcome(name, result.IsSuccess, sw.Elapsed, result.PrimaryError);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Query faulted: {Query} in {Elapsed}", name, sw.Elapsed);
            throw;
        }
    }

    private void LogOutcome(string name, bool ok, TimeSpan elapsed, ApplicationError? error)
    {
        if (ok)
        {
            _logger.LogInformation("Dispatch success: {Op} in {Elapsed}", name, elapsed);
        }
        else
        {
            _logger.LogWarning(
                "Dispatch failure: {Op} in {Elapsed} — {Code}: {Message}",
                name, elapsed, error?.Code, error?.Message);
        }
    }
}
