using Microsoft.Extensions.Logging;
using Wolverine.Configuration;

namespace Shared.EventBus.Wolverine;

/// <summary>
/// The logger Wolverine's handler discovery resolves. Discovery is disabled and every handler is registered pre-built
/// by the provider, so the two warnings discovery always emits in that configuration ("no handlers found" and "no
/// pre-generated handler registry") describe the intended setup rather than a fault. They are logged at Information;
/// every other level passes through unchanged.
/// </summary>
internal sealed class WolverineDiscoveryLogger(ILoggerFactory loggerFactory) : ILogger<HandlerDiscovery>
{
    private readonly ILogger _inner = loggerFactory.CreateLogger(typeof(HandlerDiscovery).FullName!);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(Effective(logLevel));

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _inner.Log(Effective(logLevel), eventId, state, exception, formatter);

    private static LogLevel Effective(LogLevel logLevel) =>
        logLevel == LogLevel.Warning ? LogLevel.Information : logLevel;
}
