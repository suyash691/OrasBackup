using Microsoft.Extensions.Logging;

namespace OrasBackup.Gui.Services;

/// <summary>Bridges Microsoft.Extensions.Logging to the GUI LogService tab.</summary>
public sealed class LogServiceProvider : ILoggerProvider
{
    private readonly LogService _log;
    public LogServiceProvider(LogService log) => _log = log;
    public ILogger CreateLogger(string categoryName) => new LogServiceLogger(_log, categoryName);
    public void Dispose() { }

    private sealed class LogServiceLogger(LogService log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var shortCategory = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
            log.Log($"[{shortCategory}] {formatter(state, exception)}");
        }
    }
}
