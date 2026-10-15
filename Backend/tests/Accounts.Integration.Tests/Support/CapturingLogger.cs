using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Blackglass.Accounts.IntegrationTests.Support;

/// <summary>Captures every log line at every level, including exception text.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    readonly ConcurrentQueue<string> lines = new();
    public string Text => string.Join("\n", lines);
    public ILogger CreateLogger(string category) => new Logger(category, lines);
    public void Dispose() { }

    sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> format) =>
            lines.Enqueue($"{level} {category}: {format(state, e)} {e}");
    }
}
