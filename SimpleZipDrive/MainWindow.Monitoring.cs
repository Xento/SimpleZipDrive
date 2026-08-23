using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace SimpleZipDrive;

/// <summary>
/// High-volume live monitoring UI. Kept in a separate partial so the normal window logic remains
/// unchanged and filesystem producer threads never have to wait for WPF rendering.
/// </summary>
public partial class MainWindow
{
    private const int UiLogBatchSize = 300;
    private const int MaxRenderedLogCharacters = 1_000_000;
    private const int RebuildVisibleEntryCount = 1500;

    private readonly Dictionary<LogCategory, ToggleButton> _monitorFilterButtons = [];
    private DispatcherTimer? _monitorUiTimer;
    private TextBlock? _runtimeMonitorText;
    private LoggingService? _monitorLoggingService;
    private bool _monitorUiInitialized;
    private long _lastOperationCount;
    private DateTime _lastStatsUpdateUtc = DateTime.UtcNow;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += MonitoringWindow_Loaded;
        Closed += MonitoringWindow_Closed;
    }

    private void MonitoringWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_monitorUiInitialized)
            return;

        if (_loggingService is not LoggingService monitorLoggingService)
            return;

        _monitorUiInitialized = true;
        _monitorLoggingService = monitorLoggingService;

        // The legacy handler schedules one Dispatcher operation for every collection change and
        // rebuilds the whole TextBox when the 5000-entry history trims. Replace it with one batched
        // append per timer tick.
        ((INotifyCollectionChanged)_loggingService.LogEntries).CollectionChanged -= OnLogEntriesChanged;

        InstallOperationFilterButtons();
        InstallRuntimeStatus();
        HookClearLogActions();

        _lastOperationCount = RuntimeMonitor.GetSnapshot().TotalOperations;
        _lastStatsUpdateUtc = DateTime.UtcNow;

        _monitorUiTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _monitorUiTimer.Tick += MonitoringUiTimer_Tick;
        _monitorUiTimer.Start();

        FlushPendingLogBatch();
        UpdateRuntimeStatus(force: true);
    }

    private void MonitoringWindow_Closed(object? sender, EventArgs e)
    {
        if (_monitorUiTimer == null)
            return;

        _monitorUiTimer.Stop();
        _monitorUiTimer.Tick -= MonitoringUiTimer_Tick;
        _monitorUiTimer = null;
    }

    private void InstallOperationFilterButtons()
    {
        if (MountButton.Parent is not StackPanel toolbar)
            return;

        toolbar.Children.Add(new Border
        {
            Width = 1,
            Height = 22,
            Margin = new Thickness(14, 0, 10, 0),
            Opacity = 0.35,
            Background = SystemColors.ControlTextBrush,
            VerticalAlignment = VerticalAlignment.Center
        });

        toolbar.Children.Add(new TextBlock
        {
            Text = "Trace:",
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11
        });

        AddFilterButton(toolbar, LogCategory.Open, "OPEN", "Show open/create/close file operations");
        AddFilterButton(toolbar, LogCategory.Read, "READ", "Show file read operations");
        AddFilterButton(toolbar, LogCategory.Directory, "DIR", "Show directory enumeration operations");
        AddFilterButton(toolbar, LogCategory.Metadata, "META", "Show file/volume metadata queries");
        AddFilterButton(toolbar, LogCategory.Cache, "CACHE", "Show RAM/disk cache activity");
    }

    private void AddFilterButton(Panel toolbar, LogCategory category, string label, string toolTip)
    {
        const bool enabledByDefault = false;
        RuntimeMonitor.SetCategoryVisible(category, enabledByDefault);

        var button = new ToggleButton
        {
            Content = label,
            Tag = category,
            IsChecked = enabledByDefault,
            MinWidth = 42,
            Height = 24,
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 0, 4, 0),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            ToolTip = toolTip,
            VerticalAlignment = VerticalAlignment.Center
        };

        button.Click += OperationFilterButton_Click;
        _monitorFilterButtons[category] = button;
        toolbar.Children.Add(button);
    }

    private void InstallRuntimeStatus()
    {
        if (MountStatusText.Parent is not StatusBarItem mountItem || mountItem.Parent is not StatusBar statusBar)
            return;

        _runtimeMonitorText = new TextBlock
        {
            Text = "RAM -- | Cache -- | Disk -- | I/O --",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };

        var mountIndex = statusBar.Items.IndexOf(mountItem);
        if (mountIndex < 0)
            mountIndex = statusBar.Items.Count;

        statusBar.Items.Insert(mountIndex, new Separator());
        statusBar.Items.Insert(mountIndex + 1, new StatusBarItem
        {
            Content = _runtimeMonitorText,
            ToolTip = "Live cache and file-operation statistics"
        });
    }

    private void HookClearLogActions()
    {
        if (MountButton.Parent is StackPanel toolbar)
        {
            var clearButton = toolbar.Children.OfType<Button>()
                .FirstOrDefault(static button => string.Equals(button.Content?.ToString(), "Clear Log", StringComparison.Ordinal));
            if (clearButton != null)
                clearButton.Click += MonitoringClearLog_Click;
        }

        if (LogTextBox.ContextMenu is { } contextMenu)
        {
            var clearMenu = contextMenu.Items.OfType<MenuItem>()
                .FirstOrDefault(static item => string.Equals(item.Header?.ToString(), "Clear Log", StringComparison.Ordinal));
            if (clearMenu != null)
                clearMenu.Click += MonitoringClearLog_Click;
        }
    }

    private void MonitoringClearLog_Click(object sender, RoutedEventArgs e)
    {
        // The original handler clears the visible collection. Also clear the producer queue so a
        // burst waiting for the next UI tick does not immediately reappear.
        _loggingService.Clear();
        LogTextBox.Clear();
    }

    private void OperationFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: LogCategory category } button)
            return;

        RuntimeMonitor.SetCategoryVisible(category, button.IsChecked == true);
        RebuildVisibleLog();
    }

    private void MonitoringUiTimer_Tick(object? sender, EventArgs e)
    {
        FlushPendingLogBatch();
        UpdateRuntimeStatus(force: false);
    }

    private void FlushPendingLogBatch()
    {
        var batch = _monitorLoggingService?.DrainPending(UiLogBatchSize) ?? Array.Empty<LogEntry>();
        if (batch.Count == 0)
            return;

        var visibleLines = batch
            .Where(entry => RuntimeMonitor.IsCategoryVisible(entry.Category))
            .Select(static entry => entry.ToString())
            .ToArray();

        if (visibleLines.Length == 0)
            return;

        var text = string.Join(Environment.NewLine, visibleLines);
        if (LogTextBox.Text.Length > 0)
            LogTextBox.AppendText(Environment.NewLine);

        // One append and one ScrollToEnd call for the whole batch.
        LogTextBox.AppendText(text);
        LogTextBox.ScrollToEnd();

        if (LogTextBox.Text.Length > MaxRenderedLogCharacters)
            RebuildVisibleLog();
    }

    private void RebuildVisibleLog()
    {
        var visible = _loggingService.LogEntries
            .Where(entry => RuntimeMonitor.IsCategoryVisible(entry.Category))
            .TakeLast(RebuildVisibleEntryCount)
            .Select(static entry => entry.ToString());

        LogTextBox.Text = string.Join(Environment.NewLine, visible);
        LogTextBox.ScrollToEnd();
    }

    private void UpdateRuntimeStatus(bool force)
    {
        if (_runtimeMonitorText == null)
            return;

        var now = DateTime.UtcNow;
        var elapsed = now - _lastStatsUpdateUtc;
        if (!force && elapsed < TimeSpan.FromSeconds(1))
            return;

        var snapshot = RuntimeMonitor.GetSnapshot();
        var elapsedSeconds = Math.Max(0.001, elapsed.TotalSeconds);
        var operationDelta = Math.Max(0, snapshot.TotalOperations - _lastOperationCount);
        var operationsPerSecond = operationDelta / elapsedSeconds;

        _lastOperationCount = snapshot.TotalOperations;
        _lastStatsUpdateUtc = now;

        long workingSet;
        try
        {
            using var process = Process.GetCurrentProcess();
            workingSet = process.WorkingSet64;
        }
        catch
        {
            workingSet = 0;
        }

        var cacheLimit = snapshot.MemoryCacheLimitBytes > 0
            ? $"/{FormatBytes(snapshot.MemoryCacheLimitBytes)}"
            : string.Empty;

        _runtimeMonitorText.Text =
            $"RAM {FormatBytes(workingSet)} | Cache {FormatBytes(snapshot.MemoryCacheBytes)}{cacheLimit} | " +
            $"Disk {FormatBytes(snapshot.DiskCacheBytes)} | {operationsPerSecond:F0} op/s";

        _runtimeMonitorText.ToolTip =
            $"Process working set: {FormatBytes(workingSet)}\n" +
            $"Managed GC heap: {FormatBytes(GC.GetTotalMemory(false))}\n" +
            $"RAM cache: {FormatBytes(snapshot.MemoryCacheBytes)} in {snapshot.MemoryCacheEntries} entries\n" +
            $"Configured total RAM cache limit: {FormatBytes(snapshot.MemoryCacheLimitBytes)}\n" +
            $"Per-file RAM threshold: {FormatBytes(snapshot.PerFileMemoryLimitBytes)}\n" +
            $"Disk cache: {FormatBytes(snapshot.DiskCacheBytes)} in {snapshot.DiskCacheEntries} entries\n" +
            $"Pending UI trace entries: {_monitorLoggingService?.PendingCount ?? 0}\n" +
            $"Dropped UI trace entries: {snapshot.DroppedUiEntries}\n" +
            $"Filesystem operations: {snapshot.TotalOperations} total, {operationsPerSecond:F1}/s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 MB";

        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;

        if (bytes >= gb)
            return $"{bytes / gb:F1} GB";
        if (bytes >= mb)
            return $"{bytes / mb:F0} MB";
        if (bytes >= kb)
            return $"{bytes / kb:F0} KB";
        return $"{bytes} B";
    }
}
