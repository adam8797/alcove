using System.Collections.ObjectModel;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace WpfHost;

/// <summary>Shows Alcove's log in the window: warnings and up, plus Alcove.Wpf's information and debug messages.</summary>
internal sealed class WindowLog(Dispatcher dispatcher) : ILoggerFactory
{
    public ObservableCollection<string> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    // From any thread: queued to the UI thread, never waiting for it.
    public void Write(string line) => dispatcher.BeginInvoke(() => Lines.Add($"{DateTime.Now:HH:mm:ss} {line}"));

    private sealed class Logger(WindowLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Warning || (category == "Alcove.Wpf" && logLevel >= LogLevel.Debug) || (category == "Alcove.PluginHost" && logLevel >= LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                log.Write($"{logLevel} {eventId.Id}: {formatter(state, exception)}{(exception is null ? "" : $" ({exception.Message})")}");
            }
        }
    }
}
