namespace SimpleZipDrive.Core.Models;

/// <summary>
/// Categories used by the live log/operation monitor.
/// </summary>
public enum LogCategory
{
    General,
    Open,
    Read,
    Directory,
    Metadata,
    Cache,
    Error
}

/// <summary>
/// Represents a single log message displayed in the application's log panel.
/// </summary>
public class LogEntry
{
    /// <summary>Gets the timestamp when the log entry was created.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets the log message text.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether this entry represents an error.</summary>
    public bool IsError { get; init; }

    /// <summary>Gets the live monitor category for this entry.</summary>
    public LogCategory Category { get; init; } = LogCategory.General;

    /// <summary>Returns the formatted log message with a category prefix when applicable.</summary>
    public override string ToString()
    {
        var prefix = Category switch
        {
            LogCategory.Error => "[ERROR] ",
            LogCategory.Open => "[OPEN] ",
            LogCategory.Read => "[READ] ",
            LogCategory.Directory => "[DIR] ",
            LogCategory.Metadata => "[META] ",
            LogCategory.Cache => "[CACHE] ",
            _ when IsError => "[ERROR] ",
            _ => string.Empty
        };

        return $"{prefix}{Message}";
    }
}
