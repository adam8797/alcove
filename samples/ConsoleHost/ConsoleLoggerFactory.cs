using Microsoft.Extensions.Logging;

namespace ConsoleHost;

/// <summary>A minimal console logger, so the sample needs no logging package beyond the abstractions.</summary>
internal sealed class ConsoleLoggerFactory(LogLevel minimum) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, minimum);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(string category, LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Console.WriteLine($"  [{logLevel} {category}] {formatter(state, exception)}");
            }
        }
    }
}
