namespace FlowAuto.Engine;

public class FlowLogger
{
    private const int MaxLogLines = 2000;
    private const int MaxPendingLogEntries = 4000;
    private const int MaxEntriesPerFlush = 200;
    private readonly ListBox? _listBox;
    private readonly RichTextBox? _richTextBox;
    private readonly Action<string>? _onLog;
    private readonly object _pendingEntriesLock = new();
    private readonly Queue<LogEntry> _pendingEntries = new();
    private int _flushScheduled;

    private readonly record struct LogEntry(string Level, string Line);

    public FlowLogger(ListBox? listBox = null, RichTextBox? richTextBox = null, Action<string>? onLog = null)
    {
        _listBox = listBox;
        _richTextBox = richTextBox;
        _onLog = onLog;

        // A logger can be constructed while the form is still building. If a
        // startup message arrives before the UI handle exists, schedule the
        // already-bounded queue as soon as that handle becomes usable.
        if (_listBox != null)
            _listBox.HandleCreated += (_, _) => ScheduleFlush();
        if (_richTextBox != null)
            _richTextBox.HandleCreated += (_, _) => ScheduleFlush();
    }

    public void Info(string nodeName, string message)
    {
        Log("INFO", nodeName, message);
    }

    public void Success(string nodeName, string message)
    {
        Log("OK", nodeName, message);
    }

    public void Warning(string nodeName, string message)
    {
        Log("WARN", nodeName, message);
    }

    public void Error(string nodeName, string message)
    {
        Log("ERROR", nodeName, message);
    }

    public void Retry(string nodeName, int attempt, int maxRetries, string message)
    {
        Log("RETRY", nodeName, $"[{attempt}/{maxRetries}] {message}");
    }

    private void Log(string level, string nodeName, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] [{nodeName}] {message}";

        _onLog?.Invoke(line);

        // Flow execution can generate logs from a worker thread. Queue UI work
        // instead of synchronously Invoke'ing every line, which previously made
        // capture/recognition speed depend on RichTextBox repaint throughput.
        if (_listBox == null && _richTextBox == null)
            return;

        lock (_pendingEntriesLock)
        {
            if (_pendingEntries.Count >= MaxPendingLogEntries)
                _pendingEntries.Dequeue();
            _pendingEntries.Enqueue(new LogEntry(level, line));
        }

        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
            return;

        // All UI controls passed by the application live on the same WinForms
        // UI thread. Pick one as a dispatcher and do a bounded batch per turn.
        var dispatcher = _richTextBox as Control ?? _listBox;
        if (dispatcher == null || dispatcher.IsDisposed || !dispatcher.IsHandleCreated)
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            return;
        }

        try
        {
            dispatcher.BeginInvoke((Action)FlushPendingEntries);
        }
        catch (InvalidOperationException)
        {
            // The form can close while a flow is unwinding. Retain at most the
            // bounded queue and avoid throwing from the logging path.
            Interlocked.Exchange(ref _flushScheduled, 0);
        }
    }

    private void FlushPendingEntries()
    {
        try
        {
            var entries = new List<LogEntry>(MaxEntriesPerFlush);
            lock (_pendingEntriesLock)
            {
                while (entries.Count < MaxEntriesPerFlush && _pendingEntries.Count > 0)
                    entries.Add(_pendingEntries.Dequeue());
            }

            if (entries.Count == 0)
                return;

            if (_listBox is { IsDisposed: false })
            {
                _listBox.BeginUpdate();
                try
                {
                    foreach (var entry in entries)
                        _listBox.Items.Add(entry.Line);

                    var removeCount = _listBox.Items.Count - MaxLogLines;
                    while (removeCount-- > 0)
                        _listBox.Items.RemoveAt(0);

                    if (_listBox.Items.Count > 0)
                        _listBox.TopIndex = _listBox.Items.Count - 1;
                }
                finally
                {
                    _listBox.EndUpdate();
                }
            }

            if (_richTextBox is { IsDisposed: false })
            {
                _richTextBox.SuspendLayout();
                try
                {
                    foreach (var entry in entries)
                    {
                        _richTextBox.SelectionStart = _richTextBox.TextLength;
                        _richTextBox.SelectionColor = GetColor(entry.Level);
                        _richTextBox.AppendText(entry.Line + Environment.NewLine);
                    }

                    // GetLineFromCharIndex avoids allocating RichTextBox.Lines
                    // (a complete string array) for every log update.
                    var lineCount = _richTextBox.GetLineFromCharIndex(_richTextBox.TextLength) + 1;
                    if (lineCount > MaxLogLines)
                    {
                        var trimAt = _richTextBox.GetFirstCharIndexFromLine(lineCount - MaxLogLines);
                        if (trimAt > 0)
                        {
                            _richTextBox.Select(0, trimAt);
                            _richTextBox.SelectedText = string.Empty;
                        }
                    }

                    _richTextBox.SelectionColor = _richTextBox.ForeColor;
                    _richTextBox.ScrollToCaret();
                }
                finally
                {
                    _richTextBox.ResumeLayout();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            var hasPendingEntries = false;
            lock (_pendingEntriesLock)
            {
                hasPendingEntries = _pendingEntries.Count > 0;
            }
            if (hasPendingEntries)
                ScheduleFlush();
        }
    }

    private static Color GetColor(string level) => level switch
    {
        "OK" => AppTheme.Success,
        "WARN" or "RETRY" => AppTheme.Warning,
        "ERROR" => AppTheme.Danger,
        _ => Color.FromArgb(185, 196, 215)
    };
}
