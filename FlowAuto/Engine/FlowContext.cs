namespace FlowAuto.Engine;

public class FlowContext
{
    private readonly AsyncLocal<CancellationToken?> _activeCancellationToken = new();

    public Dictionary<string, object> Variables { get; } = new();
    public IntPtr CurrentHwnd { get; set; }
    public CancellationTokenSource? Cts { get; set; }
    public CancellationToken FlowCancellationToken =>
        Cts?.Token ?? System.Threading.CancellationToken.None;
    public CancellationToken CancellationToken =>
        _activeCancellationToken.Value ?? FlowCancellationToken;
    public TaskCompletionSource<bool>? PauseTcs { get; set; }
    public FlowLogger Logger { get; }

    // Execution state
    public bool IsPaused { get; set; }
    public bool IsStopping { get; set; }
    public int CurrentNodeIndex { get; set; }

    public FlowContext(FlowLogger logger)
    {
        Logger = logger;
    }

    public void SetHwnd(IntPtr hwnd)
    {
        CurrentHwnd = hwnd;
        Variables["TargetHwnd"] = hwnd;
    }

    public T? Get<T>(string key)
    {
        if (Variables.TryGetValue(key, out var val) && val is T t)
            return t;
        return default;
    }

    public void Set(string key, object value)
    {
        Variables[key] = value;
    }

    /// <summary>
    /// Check if cancellation is requested. Throws if stopped.
    /// </summary>
    public void CheckCancellation()
    {
        CancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Apply a per-node timeout token to all asynchronous operations in the
    /// current execution path without changing the flow-wide stop token.
    /// </summary>
    public IDisposable UseCancellationToken(CancellationToken cancellationToken)
    {
        var previous = _activeCancellationToken.Value;
        _activeCancellationToken.Value = cancellationToken;
        return new CancellationScope(_activeCancellationToken, previous);
    }

    /// <summary>
    /// Delay while observing a user-requested stop. Centralizing delays keeps
    /// retries, polling loops and pre/post action waits responsive.
    /// </summary>
    public Task DelayAsync(int milliseconds)
    {
        if (milliseconds <= 0)
        {
            CheckCancellation();
            return Task.CompletedTask;
        }

        return Task.Delay(milliseconds, CancellationToken);
    }

    /// <summary>
    /// Wait if paused. Returns when resumed or throws if stopped.
    /// </summary>
    public async Task WaitIfPausedAsync()
    {
        while (IsPaused)
        {
            CheckCancellation();
            if (PauseTcs != null)
                await PauseTcs.Task.WaitAsync(CancellationToken);
            else
                await DelayAsync(100);
        }
    }

    private sealed class CancellationScope(
        AsyncLocal<CancellationToken?> target,
        CancellationToken? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            target.Value = previous;
            _disposed = true;
        }
    }
}
