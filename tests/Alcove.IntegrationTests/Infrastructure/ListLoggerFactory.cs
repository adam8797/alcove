using Microsoft.Extensions.Logging;

namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>A log entry captured by <see cref="ListLoggerFactory"/>.</summary>
internal sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message);

/// <summary>Captures every log entry (all levels) so tests can assert on warnings and event IDs.</summary>
internal sealed class ListLoggerFactory : ILoggerFactory
{
    private readonly List<LogEntry> _entries = [];

    /// <summary>Runs on the logging thread for every entry, before it's stored (e.g. to check which locks are held).</summary>
    public Action<LogEntry>? Observer { get; set; }

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public IEnumerable<LogEntry> WithEventId(int id) => Entries.Where(e => e.EventId.Id == id);

    public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, this);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private void Add(LogEntry entry)
    {
        Observer?.Invoke(entry);
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    private sealed class ListLogger(string category, ListLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Add(new LogEntry(category, logLevel, eventId, formatter(state, exception)));
    }
}
