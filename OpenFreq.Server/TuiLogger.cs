using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OpenFreq.Server;

public class TuiLogMessage
{
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Category { get; set; }
}

public class TuiLogger : ILogger
{
    private readonly string _categoryName;
    private readonly ConcurrentQueue<TuiLogMessage> _logMessages;
    private readonly int _maxMessages;

    public TuiLogger(string categoryName, ConcurrentQueue<TuiLogMessage> logMessages, int maxMessages = 100)
    {
        _categoryName = categoryName;
        _logMessages = logMessages;
        _maxMessages = maxMessages;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        if (exception != null)
        {
            message += $" | Exception: {exception.Message}";
        }

        _logMessages.Enqueue(new TuiLogMessage
        {
            Timestamp = DateTime.UtcNow,
            Level = logLevel,
            Message = message,
            Category = _categoryName
        });

        // Keep only the last N messages
        while (_logMessages.Count > _maxMessages)
        {
            _logMessages.TryDequeue(out _);
        }
    }
}

public class TuiLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<TuiLogMessage> _logMessages;
    private readonly int _maxMessages;

    public TuiLoggerProvider(ConcurrentQueue<TuiLogMessage> logMessages, int maxMessages = 100)
    {
        _logMessages = logMessages;
        _maxMessages = maxMessages;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new TuiLogger(categoryName, _logMessages, _maxMessages);
    }

    public void Dispose() { }
}
