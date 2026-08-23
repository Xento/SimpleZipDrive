using System.Collections.ObjectModel;
using System.Windows;

namespace SimpleZipDrive.Core.Services;

/// <summary>
/// Implementation of the logging service. Producer threads never dispatch one UI operation per
/// log line; when a WPF dispatcher exists, entries are queued and drained by the window in batches.
/// </summary>
public class LoggingService : ILoggingService
{
    private const int MaxLogEntries = 5000;
    private const int MaxPendingEntries = 20000;

    private readonly object _lock = new();
    private readonly ConcurrentQueue<LogEntry> _pendingEntries = new();
    private int _pendingCount;

    /// <inheritdoc />
    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    /// <inheritdoc />
    public int PendingCount => Math.Max(0, Volatile.Read(ref _pendingCount));

    /// <inheritdoc />
    public void Log(string message)
    {
        if (message is null) return;

        var category = RuntimeMonitor.ClassifyApplicationMessage(message);
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Message = message.TrimEnd('\r', '\n'),
            IsError = false,
            Category = category
        };

        if (category == LogCategory.General || RuntimeMonitor.IsCategoryVisible(category))
            AddOrQueue(entry);

        if (entry.Message.Length > 0)
            Serilog.Log.Information("{LogMessage}", entry.Message);
    }

    /// <inheritdoc />
    public void LogOperation(LogCategory category, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || !RuntimeMonitor.IsCategoryVisible(category))
            return;

        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Message = message.TrimEnd('\r', '\n'),
            IsError = false,
            Category = category
        };

        AddOrQueue(entry);
        // The complete operation trace is already written by DiagnosticLogger when enabled.
        // Do not mirror high-volume filesystem operations to the normal Information pipeline.
    }

    /// <inheritdoc />
    public void LogError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Message = message.TrimEnd('\r', '\n'),
            IsError = true,
            Category = LogCategory.Error
        };

        AddOrQueue(entry);
        Serilog.Log.Error("{LogMessage}", entry.Message);
    }

    /// <inheritdoc />
    public IReadOnlyList<LogEntry> DrainPending(int maxEntries)
    {
        if (maxEntries <= 0 || PendingCount == 0)
            return [];

        var drained = new List<LogEntry>(Math.Min(maxEntries, PendingCount));
        lock (_lock)
        {
            while (drained.Count < maxEntries && _pendingEntries.TryDequeue(out var entry))
            {
                Interlocked.Decrement(ref _pendingCount);
                if (AddEntryCore(entry))
                    drained.Add(entry);
            }
        }

        return drained;
    }

    /// <inheritdoc />
    public void Clear()
    {
        while (_pendingEntries.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingCount);
        }

        Interlocked.Exchange(ref _pendingCount, 0);
        lock (_lock)
        {
            LogEntries.Clear();
        }
    }

    /// <inheritdoc />
    public string GetAllLogsAsText()
    {
        LogEntry[] retained;
        lock (_lock)
        {
            retained = LogEntries.ToArray();
        }

        var pending = _pendingEntries.ToArray();
        return string.Join(Environment.NewLine, retained.Concat(pending).Select(static e => e.ToString()));
    }

    private void AddOrQueue(LogEntry entry)
    {
        // Unit tests and non-WPF consumers have no Application dispatcher. Preserve the original
        // synchronous behavior there so LoggingService remains usable outside the GUI.
        if (Application.Current?.Dispatcher == null)
        {
            lock (_lock)
            {
                AddEntryCore(entry);
            }
            return;
        }

        _pendingEntries.Enqueue(entry);
        var pending = Interlocked.Increment(ref _pendingCount);

        // Bound the GUI backlog. Dropping old display-only trace lines is preferable to allowing
        // Explorer/Defender I/O bursts to consume memory or stall filesystem callbacks.
        while (pending > MaxPendingEntries && _pendingEntries.TryDequeue(out _))
        {
            pending = Interlocked.Decrement(ref _pendingCount);
            RuntimeMonitor.RecordDroppedUiEntry();
        }
    }

    private bool AddEntryCore(LogEntry entry)
    {
        if (LogEntries.Count > 0)
        {
            var lastEntry = LogEntries[^1];
            if (lastEntry.Message == entry.Message &&
                lastEntry.Category == entry.Category &&
                (entry.Timestamp - lastEntry.Timestamp).TotalMilliseconds < 100)
                return false;
        }

        LogEntries.Add(entry);
        while (LogEntries.Count > MaxLogEntries)
            LogEntries.RemoveAt(0);

        return true;
    }
}
