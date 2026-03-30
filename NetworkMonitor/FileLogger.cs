using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

[ProviderAlias("File")]
sealed class FileLoggerProvider(string logDirectory) : ILoggerProvider
{
    private readonly object _lock = new();

    public ILogger CreateLogger(string categoryName)
    {
        Directory.CreateDirectory(logDirectory);
        return new FileLogger(categoryName, logDirectory, _lock);
    }

    public void Dispose() { }
}

sealed class FileLogger(string categoryName, string logDirectory, object fileLock) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} [{logLevel,-11}] {categoryName}: {message}";

        if (exception != null)
            line += Environment.NewLine + exception;

        var filePath = Path.Combine(logDirectory, $"networkmonitor-{DateTime.UtcNow:yyyy-MM-dd}.log");

        lock (fileLock)
            File.AppendAllText(filePath, line + Environment.NewLine);
    }
}
