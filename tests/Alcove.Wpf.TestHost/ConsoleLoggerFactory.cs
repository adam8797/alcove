using Microsoft.Extensions.Logging;

namespace Alcove.Wpf.TestHost;

/// <summary>Writes Alcove's log to the console as <c>LOG &lt;level&gt; &lt;event&gt; &lt;category&gt;: message</c> lines (debug and up), for the test's output.</summary>
internal sealed class ConsoleLoggerFactory : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class ConsoleLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Console.WriteLine($"LOG {logLevel} {eventId.Id} {category}: {formatter(state, exception)}{(exception is null ? "" : $" [{exception.GetType().Name}: {exception.Message}]")}");
            }
        }
    }
}
