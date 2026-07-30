using Microsoft.Extensions.Logging;
using Sylnar.Helpers;
using Sylnar.Models;

namespace Sylnar.Services;

public class LogService
{
    private readonly ILogger<LogService> _logger;
    private readonly List<LogEntry> _entries = new();
    public IReadOnlyList<LogEntry> Entries => _entries.AsReadOnly();

    public event EventHandler<LogEntry>? LogAdded;

    public LogService(ILogger<LogService> logger)
    {
        _logger = logger;
    }

    public void Info(string message)
    {
        AddEntry("INFO", message);
        _logger.LogInformation("{Message}", message);
    }

    public void Warn(string message)
    {
        AddEntry("WARN", message);
        _logger.LogWarning("{Message}", message);
    }

    public void Error(string message)
    {
        AddEntry("ERROR", message);
        _logger.LogError("{Message}", message);
    }

    public void Debug(string message)
    {
        AddEntry("DEBUG", message);
        _logger.LogDebug("{Message}", message);
    }

    private void AddEntry(string level, string message)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Message = message
        };
        _entries.Add(entry);
        LogAdded?.Invoke(this, entry);
    }
}

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Formatted => $"[{Timestamp:HH:mm:ss}] [{Level}] {Message}";
}
