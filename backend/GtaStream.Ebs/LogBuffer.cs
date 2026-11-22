using System.Collections.Concurrent;

namespace GtaStream.Ebs;

/// <summary>One log line as /api/logs returns it.</summary>
public sealed record LogEntry(long Id, DateTimeOffset Time, string Level, string Category, string Message, string? Exception);

/// <summary>
/// Keeps the newest log lines in memory so /api/logs can show them without shell access to the server.
/// Lost on restart; the systemd journal keeps the full history.
/// </summary>
public sealed class LogBuffer
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly int _capacity;
    private long _nextId;

    public LogBuffer(int capacity = 2000) => _capacity = capacity;

    public void Add(LogLevel level, string category, string message, Exception? exception)
    {
        var id = Interlocked.Increment(ref _nextId);
        _entries.Enqueue(new LogEntry(id, DateTimeOffset.UtcNow, level.ToString(), category, message, exception?.ToString()));
        while (_entries.Count > _capacity && _entries.TryDequeue(out _)) { }
    }

    /// <summary>Newest entries last; filtered by minimum level and, for polling, by id.</summary>
    public IReadOnlyList<LogEntry> Read(int limit, LogLevel minLevel, long afterId)
    {
        var list = _entries
            .Where(e => e.Id > afterId && Enum.Parse<LogLevel>(e.Level) >= minLevel)
            .ToList();
        return list.Skip(Math.Max(0, list.Count - limit)).ToList();
    }
}

public sealed class LogBufferProvider(LogBuffer buffer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BufferLogger(buffer, categoryName);
    public void Dispose() { }

    private sealed class BufferLogger(LogBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) buffer.Add(logLevel, category, formatter(state, exception), exception);
        }
    }
}
