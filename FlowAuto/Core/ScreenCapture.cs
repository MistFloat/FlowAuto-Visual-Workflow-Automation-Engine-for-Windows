using System.Drawing.Imaging;

namespace FlowAuto.Core;

public static class ScreenCapture
{
    /// <summary>
    /// Capture the client area of a window.
    /// </summary>
    public static Bitmap? CaptureWindow(IntPtr hWnd)
    {
        var (clientLeft, clientTop, clientWidth, clientHeight) = WindowHelper.GetClientBounds(hWnd);

        if (clientWidth <= 0 || clientHeight <= 0) return null;

        var bmp = new Bitmap(clientWidth, clientHeight, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(clientLeft, clientTop, 0, 0, new Size(clientWidth, clientHeight));

        return bmp;
    }

    /// <summary>
    /// Capture a specific screen region.
    /// </summary>
    public static Bitmap CaptureRegion(int screenX, int screenY, int width, int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Capture width must be greater than zero.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Capture height must be greater than zero.");

        var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(screenX, screenY, 0, 0, new Size(width, height));
        return bmp;
    }

    /// <summary>
    /// Capture a region relative to the window client area.
    /// </summary>
    public static Bitmap? CaptureWindowRegion(IntPtr hWnd, int regionX, int regionY, int regionWidth, int regionHeight)
    {
        var (clientLeft, clientTop, clientWidth, clientHeight) = WindowHelper.GetClientBounds(hWnd);

        if (!TryClampRegionToClientBounds(
                clientWidth, clientHeight, regionX, regionY, regionWidth, regionHeight,
                out var clampedRegion))
            return null;

        return CaptureRegion(
            clientLeft + clampedRegion.X,
            clientTop + clampedRegion.Y,
            clampedRegion.Width,
            clampedRegion.Height);
    }

    /// <summary>
    /// Clamp a region expressed in client coordinates to the current client bounds.
    /// This is intentionally separate from screen capture so polling callers can
    /// validate their requested size without allocating a bitmap.
    /// </summary>
    public static bool TryClampRegionToClientBounds(
        int clientWidth, int clientHeight,
        int regionX, int regionY, int regionWidth, int regionHeight,
        out Rectangle clampedRegion)
    {
        clampedRegion = Rectangle.Empty;
        if (clientWidth <= 0 || clientHeight <= 0 ||
            regionX < 0 || regionY < 0 || regionWidth <= 0 || regionHeight <= 0 ||
            regionX >= clientWidth || regionY >= clientHeight)
            return false;

        // Avoid addition overflow for untrusted or legacy flow values.
        int width = Math.Min(regionWidth, clientWidth - regionX);
        int height = Math.Min(regionHeight, clientHeight - regionY);
        if (width <= 0 || height <= 0)
            return false;

        clampedRegion = new Rectangle(regionX, regionY, width, height);
        return true;
    }

    /// <summary>
    /// Save a bitmap to file.
    /// </summary>
    public static void SaveBitmap(Bitmap bmp, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        bmp.Save(filePath, ImageFormat.Png);
    }
}

/// <summary>
/// Reuses a GDI bitmap and graphics surface while repeatedly capturing the same
/// region of a window. The returned bitmap is owned by this session and remains
/// valid only until the next <see cref="Capture"/> call or disposal of the
/// session; callers must not dispose it.
/// </summary>
public sealed class WindowRegionCaptureSession : IDisposable
{
    private readonly IntPtr _hWnd;
    private readonly int _regionX;
    private readonly int _regionY;
    private readonly int _regionWidth;
    private readonly int _regionHeight;
    private Bitmap? _buffer;
    private Graphics? _graphics;
    private bool _disposed;

    public WindowRegionCaptureSession(
        IntPtr hWnd, int regionX, int regionY, int regionWidth, int regionHeight)
    {
        _hWnd = hWnd;
        _regionX = regionX;
        _regionY = regionY;
        _regionWidth = regionWidth;
        _regionHeight = regionHeight;
    }

    /// <summary>
    /// Capture the latest image into the reusable buffer. A window resize or a
    /// clipped region rebuilds the buffer to the new dimensions.
    /// </summary>
    public Bitmap? Capture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var (clientLeft, clientTop, clientWidth, clientHeight) =
            WindowHelper.GetClientBounds(_hWnd);
        if (!ScreenCapture.TryClampRegionToClientBounds(
                clientWidth, clientHeight,
                _regionX, _regionY, _regionWidth, _regionHeight,
                out var clampedRegion))
            return null;

        EnsureBuffer(clampedRegion.Size);
        _graphics!.CopyFromScreen(
            clientLeft + clampedRegion.X,
            clientTop + clampedRegion.Y,
            0,
            0,
            clampedRegion.Size);
        return _buffer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _graphics?.Dispose();
        _buffer?.Dispose();
        _graphics = null;
        _buffer = null;
        _disposed = true;
    }

    private void EnsureBuffer(Size size)
    {
        if (_buffer is { Width: var width, Height: var height } &&
            width == size.Width && height == size.Height)
            return;

        _graphics?.Dispose();
        _buffer?.Dispose();
        _buffer = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        _graphics = Graphics.FromImage(_buffer);
    }
}
