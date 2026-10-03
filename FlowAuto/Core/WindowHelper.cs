using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FlowAuto.Core;

public static class WindowHelper
{
    // P/Invoke declarations
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// Find a window by title keyword (Contains match).
    /// </summary>
    public static IntPtr FindWindowByTitle(string titleKeyword)
    {
        IntPtr foundHwnd = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            int length = GetWindowTextLength(hWnd);
            if (length == 0) return true;

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            if (title.Contains(titleKeyword, StringComparison.OrdinalIgnoreCase) && IsWindowVisible(hWnd))
            {
                foundHwnd = hWnd;
                return false; // stop enumeration
            }
            return true;
        }, IntPtr.Zero);

        return foundHwnd;
    }

    /// <summary>
    /// Get the client area in screen coordinates.
    /// Returns (Left, Top, Width, Height).
    /// </summary>
    public static (int Left, int Top, int Width, int Height) GetClientBounds(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !GetClientRect(hWnd, out RECT clientRect))
            return (0, 0, 0, 0);

        var pt = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hWnd, ref pt))
            return (0, 0, 0, 0);

        return (pt.X, pt.Y, clientRect.Width, clientRect.Height);
    }

    /// <summary>
    /// Activate and bring window to foreground.
    /// </summary>
    public static bool ActivateWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;
        return SetForegroundWindow(hWnd);
    }

    /// <summary>
    /// Wait for a window with the given title keyword to appear.
    /// </summary>
    public static IntPtr WaitForWindow(string titleKeyword, int timeoutMs, int checkIntervalMs = 500)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var hWnd = FindWindowByTitle(titleKeyword);
            if (hWnd != IntPtr.Zero) return hWnd;
            Thread.Sleep(checkIntervalMs);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Wait for a window without blocking a worker thread, while observing stop requests.
    /// </summary>
    public static async Task<IntPtr> WaitForWindowAsync(
        string titleKeyword, int timeoutMs, int checkIntervalMs = 500,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(titleKeyword))
            throw new ArgumentException("Window title keyword is required.", nameof(titleKeyword));
        if (timeoutMs < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        if (checkIntervalMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(checkIntervalMs));

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hWnd = FindWindowByTitle(titleKeyword);
            if (hWnd != IntPtr.Zero) return hWnd;

            var remaining = timeoutMs - (int)Math.Min(timeoutMs, stopwatch.ElapsedMilliseconds);
            await Task.Delay(Math.Min(checkIntervalMs, Math.Max(1, remaining)), cancellationToken);
        }

        return IntPtr.Zero;
    }
}
