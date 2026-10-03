using System.Runtime.InteropServices;

namespace FlowAuto.Core;

public static class InputSimulator
{
    // Win32 mouse_event
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    // Win32 keybd_event
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // Mouse event constants
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    // Keyboard event constants
    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    // Scan code mapping
    private static readonly Dictionary<string, byte> ScanCodeMap = new()
    {
        ["A"] = 0x1E, ["B"] = 0x30, ["C"] = 0x2E, ["D"] = 0x20,
        ["E"] = 0x12, ["F"] = 0x21, ["G"] = 0x22, ["H"] = 0x23,
        ["I"] = 0x17, ["J"] = 0x24, ["K"] = 0x25, ["L"] = 0x26,
        ["M"] = 0x32, ["N"] = 0x31, ["O"] = 0x18, ["P"] = 0x19,
        ["Q"] = 0x10, ["R"] = 0x13, ["S"] = 0x1F, ["T"] = 0x14,
        ["U"] = 0x16, ["V"] = 0x2F, ["W"] = 0x11, ["X"] = 0x2D,
        ["Y"] = 0x15, ["Z"] = 0x2C,
        ["0"] = 0x0B, ["1"] = 0x02, ["2"] = 0x03, ["3"] = 0x04,
        ["4"] = 0x05, ["5"] = 0x06, ["6"] = 0x07, ["7"] = 0x08,
        ["8"] = 0x09, ["9"] = 0x0A,
        ["ESC"] = 0x01, ["SPACE"] = 0x39, ["ENTER"] = 0x1C,
        ["TAB"] = 0x0F, ["SHIFT"] = 0x2A, ["CTRL"] = 0x1D,
        ["F1"] = 0x3B, ["F2"] = 0x3C, ["F3"] = 0x3D, ["F4"] = 0x3E,
        ["F5"] = 0x3F, ["F6"] = 0x40, ["F7"] = 0x41, ["F8"] = 0x42,
        ["F9"] = 0x43, ["F10"] = 0x44, ["F11"] = 0x57, ["F12"] = 0x58,
    };

    /// <summary>
    /// Get scan code by key name (case-insensitive).
    /// </summary>
    public static byte GetScanCode(string keyName)
    {
        if (ScanCodeMap.TryGetValue(keyName.ToUpper(), out var sc)) return sc;
        throw new ArgumentException($"Unknown key name: {keyName}");
    }

    /// <summary>
    /// Check if a scan code is present in the known mapping.
    /// </summary>
    public static bool IsKnownScanCode(byte scanCode)
    {
        return ScanCodeMap.Values.Contains(scanCode);
    }

    /// <summary>
    /// Get key name by scan code (case-insensitive reverse lookup).
    /// </summary>
    public static string? GetKeyName(byte scanCode)
    {
        foreach (var kvp in ScanCodeMap)
        {
            if (kvp.Value == scanCode) return kvp.Key;
        }
        return null;
    }

    /// <summary>
    /// Move cursor to absolute screen position and perform left click.
    /// </summary>
    public static void MoveAndClick(int screenX, int screenY, int preDelayMs = 100, int postDelayMs = 500)
    {
        Cursor.Position = new Point(screenX, screenY);
        if (preDelayMs > 0) Thread.Sleep(preDelayMs);

        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);

        if (postDelayMs > 0) Thread.Sleep(postDelayMs);
    }

    public static async Task MoveAndClickAsync(
        int screenX, int screenY, int preDelayMs = 100, int postDelayMs = 500,
        CancellationToken cancellationToken = default)
    {
        Cursor.Position = new Point(screenX, screenY);
        if (preDelayMs > 0)
            await Task.Delay(preDelayMs, cancellationToken);

        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        try
        {
            await Task.Delay(50, cancellationToken);
        }
        finally
        {
            // Never leave the mouse button held if a stop/timeout arrives.
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        if (postDelayMs > 0)
            await Task.Delay(postDelayMs, cancellationToken);
    }

    /// <summary>
    /// Press and release a key using scan code mode.
    /// </summary>
    public static void PressKey(byte scanCode)
    {
        keybd_event(0, scanCode, KEYEVENTF_SCANCODE | KEYEVENTF_KEYDOWN, UIntPtr.Zero);
        Thread.Sleep(50);
        keybd_event(0, scanCode, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static async Task PressKeyAsync(byte scanCode, CancellationToken cancellationToken = default)
    {
        keybd_event(0, scanCode, KEYEVENTF_SCANCODE | KEYEVENTF_KEYDOWN, UIntPtr.Zero);
        try
        {
            await Task.Delay(50, cancellationToken);
        }
        finally
        {
            keybd_event(0, scanCode, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }

    /// <summary>
    /// Hold a key down.
    /// </summary>
    public static void KeyDown(byte scanCode)
    {
        keybd_event(0, scanCode, KEYEVENTF_SCANCODE | KEYEVENTF_KEYDOWN, UIntPtr.Zero);
    }

    /// <summary>
    /// Release a key.
    /// </summary>
    public static void KeyUp(byte scanCode)
    {
        keybd_event(0, scanCode, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>
    /// Hold a key for a specified duration, then release.
    /// </summary>
    public static void HoldKey(byte scanCode, int holdDurationMs)
    {
        keybd_event(0, scanCode, KEYEVENTF_SCANCODE | KEYEVENTF_KEYDOWN, UIntPtr.Zero);
        Thread.Sleep(holdDurationMs);
        keybd_event(0, scanCode, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static async Task HoldKeyAsync(
        byte scanCode, int holdDurationMs, CancellationToken cancellationToken = default)
    {
        if (holdDurationMs < 0)
            throw new ArgumentOutOfRangeException(nameof(holdDurationMs));

        keybd_event(0, scanCode, KEYEVENTF_SCANCODE | KEYEVENTF_KEYDOWN, UIntPtr.Zero);
        try
        {
            await Task.Delay(holdDurationMs, cancellationToken);
        }
        finally
        {
            keybd_event(0, scanCode, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }
}
