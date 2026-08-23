using System.Globalization;
using System.Text.RegularExpressions;

namespace SimpleZipDrive.Core;

/// <summary>
/// Lightweight process-local telemetry for the live file-operation/cache monitor.
/// Filesystem producer threads only update atomics and enqueue display events; they never wait
/// for WPF rendering.
/// </summary>
public static class RuntimeMonitor
{
    private const int DefaultTraceCategoriesMask = 0;

    private static readonly Regex LimitRegex = new(@"(?<size>[0-9.,]+)\s+MB", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static long _totalOperations;
    private static long _droppedUiEntries;
    private static long _memoryCacheBytes;
    private static long _diskCacheBytes;
    private static long _memoryCacheLimitBytes;
    private static long _perFileMemoryLimitBytes;
    private static int _memoryCacheEntries;
    private static int _diskCacheEntries;
    private static int _visibleCategoryMask = DefaultTraceCategoriesMask;

    /// <summary>Starts telemetry for a newly mounted archive.</summary>
    public static void ResetForMount(long memoryCacheLimitBytes, long perFileMemoryLimitBytes)
    {
        Interlocked.Exchange(ref _totalOperations, 0);
        Interlocked.Exchange(ref _droppedUiEntries, 0);
        Interlocked.Exchange(ref _memoryCacheBytes, 0);
        Interlocked.Exchange(ref _diskCacheBytes, 0);
        Interlocked.Exchange(ref _memoryCacheEntries, 0);
        Interlocked.Exchange(ref _diskCacheEntries, 0);
        Interlocked.Exchange(ref _memoryCacheLimitBytes, Math.Max(0, memoryCacheLimitBytes));
        Interlocked.Exchange(ref _perFileMemoryLimitBytes, Math.Max(0, perFileMemoryLimitBytes));
    }

    /// <summary>Publishes the exact shared in-memory entry cache state from ZipFileSystemCore.</summary>
    public static void UpdateMemoryCache(long bytes, int entries)
    {
        Interlocked.Exchange(ref _memoryCacheBytes, Math.Max(0, bytes));
        Interlocked.Exchange(ref _memoryCacheEntries, Math.Max(0, entries));
    }

    /// <summary>Publishes the exact temporary disk-cache state from ZipFileSystemCore.</summary>
    public static void UpdateDiskCache(long bytes, int entries)
    {
        Interlocked.Exchange(ref _diskCacheBytes, Math.Max(0, bytes));
        Interlocked.Exchange(ref _diskCacheEntries, Math.Max(0, entries));
    }

    /// <summary>Clears cache counters after unmount/dispose.</summary>
    public static void ClearCacheStats()
    {
        UpdateMemoryCache(0, 0);
        UpdateDiskCache(0, 0);
    }

    /// <summary>Captures an internal filesystem diagnostic line and forwards recognized operations to the UI logger.</summary>
    public static void CaptureDiagnosticMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var trimmed = message.Trim();

        // Keep these parsers as a fallback for diagnostics created before the core has published
        // its exact values. ZipFileSystemCore.ResetForMount remains authoritative.
        if (trimmed.StartsWith("Max memory cache:", StringComparison.OrdinalIgnoreCase))
        {
            if (TryReadLimit(trimmed, out var bytes))
                Interlocked.Exchange(ref _perFileMemoryLimitBytes, bytes);
            return;
        }

        if (trimmed.StartsWith("Max total memory:", StringComparison.OrdinalIgnoreCase))
        {
            if (TryReadLimit(trimmed, out var bytes))
                Interlocked.Exchange(ref _memoryCacheLimitBytes, bytes);
            return;
        }

        if (!TryClassifyFileOperation(trimmed, out var category))
            return;

        Interlocked.Increment(ref _totalOperations);
        if (!IsCategoryVisible(category))
            return;

        if (ServiceProvider.TryGet<ILoggingService>() is LoggingService loggingService)
            loggingService.LogOperation(category, trimmed);
    }

    /// <summary>Classifies normal application messages so cache activity can be filtered separately.</summary>
    public static LogCategory ClassifyApplicationMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return LogCategory.General;

        return IsCacheMessage(message) ? LogCategory.Cache : LogCategory.General;
    }

    /// <summary>Returns whether a live trace category is currently enabled in the UI.</summary>
    public static bool IsCategoryVisible(LogCategory category)
    {
        if (category is LogCategory.General or LogCategory.Error)
            return true;

        var bit = 1 << (int)category;
        return (Volatile.Read(ref _visibleCategoryMask) & bit) != 0;
    }

    /// <summary>Enables or disables one live trace category.</summary>
    public static void SetCategoryVisible(LogCategory category, bool visible)
    {
        if (category is LogCategory.General or LogCategory.Error)
            return;

        var bit = 1 << (int)category;
        while (true)
        {
            var current = Volatile.Read(ref _visibleCategoryMask);
            var updated = visible ? current | bit : current & ~bit;
            if (Interlocked.CompareExchange(ref _visibleCategoryMask, updated, current) == current)
                return;
        }
    }

    /// <summary>Records that a display-only entry was dropped because the UI backlog was full.</summary>
    public static void RecordDroppedUiEntry()
    {
        Interlocked.Increment(ref _droppedUiEntries);
    }

    /// <summary>Gets a lock-free snapshot for the status bar.</summary>
    public static RuntimeMonitorSnapshot GetSnapshot()
    {
        return new RuntimeMonitorSnapshot(
            Volatile.Read(ref _totalOperations),
            Volatile.Read(ref _droppedUiEntries),
            Volatile.Read(ref _memoryCacheBytes),
            Volatile.Read(ref _memoryCacheEntries),
            Volatile.Read(ref _diskCacheBytes),
            Volatile.Read(ref _diskCacheEntries),
            Volatile.Read(ref _memoryCacheLimitBytes),
            Volatile.Read(ref _perFileMemoryLimitBytes));
    }

    private static bool TryClassifyFileOperation(string message, out LogCategory category)
    {
        category = LogCategory.General;
        var operation = message;
        var colonIndex = operation.IndexOf(':');
        if (colonIndex > 0)
            operation = operation[..colonIndex];

        operation = operation.Trim();
        switch (operation)
        {
            case "CreateFile":
            case "Open":
            case "Create":
            case "OpenOrCreateFile":
            case "Cleanup":
            case "CloseFile":
            case "Close":
                category = LogCategory.Open;
                return true;

            case "ReadFile":
            case "Read":
                category = LogCategory.Read;
                return true;

            case "FindFiles":
            case "FindFilesWithPattern":
            case "ReadDirectory":
            case "ReadDirectoryEntry":
            case "FindStreams":
                category = LogCategory.Directory;
                return true;

            case "GetFileInformation":
            case "GetFileInfo":
            case "GetDirInfoByName":
            case "GetVolumeInformation":
            case "GetDiskFreeSpace":
            case "GetSecurity":
            case "GetSecurityByName":
            case "GetFileSecurity":
                category = LogCategory.Metadata;
                return true;

            default:
                return false;
        }
    }

    private static bool IsCacheMessage(string message)
    {
        return message.Contains("Memory cache", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("disk cache", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Stored entry detected", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("temporary cache", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Large file detected", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Extraction complete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadLimit(string message, out long bytes)
    {
        bytes = 0;
        var match = LimitRegex.Match(message);
        if (!match.Success)
            return false;

        var normalized = match.Groups["size"].Value.Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) || mb < 0)
            return false;

        bytes = (long)(mb * 1024d * 1024d);
        return true;
    }
}

/// <summary>Snapshot of live cache and filesystem operation telemetry.</summary>
public readonly record struct RuntimeMonitorSnapshot(
    long TotalOperations,
    long DroppedUiEntries,
    long MemoryCacheBytes,
    int MemoryCacheEntries,
    long DiskCacheBytes,
    int DiskCacheEntries,
    long MemoryCacheLimitBytes,
    long PerFileMemoryLimitBytes);
