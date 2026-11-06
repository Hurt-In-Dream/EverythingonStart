using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EverythingonStart.Services;

public enum DesktopWallpaperLayout { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }
public sealed record DesktopWallpaperMonitor(Rect Bounds, string ImagePath);
public sealed record DesktopWallpaperState(DesktopWallpaperLayout Layout, Color Background,
    IReadOnlyList<DesktopWallpaperMonitor> Monitors, bool Enabled = true);
public sealed record DesktopWallpaperImage(BitmapSource Bitmap, Size NaturalSize);

// Reads Explorer's configured wallpaper layer. No screen/window capture, Explorer injection or setters.
public sealed class DesktopWallpaperService : IDisposable
{
    private readonly Func<DesktopWallpaperState?> _stateReader;
    private readonly Func<string, DesktopWallpaperImage?> _imageReader;
    private readonly Dictionary<string, (DateTime Changed, long Length, DesktopWallpaperImage Image)> _images = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private string _lastReadFailure = string.Empty;

    public DesktopWallpaperService(Func<DesktopWallpaperState?>? stateReader = null, Func<string, DesktopWallpaperImage?>? imageReader = null)
    {
        _stateReader = stateReader ?? ReadWindowsWallpaper;
        _imageReader = imageReader ?? LoadImage;
    }

    public BitmapSource? ReadRegion(Rect region)
    {
        if (_disposed) return null;
        _lastReadFailure = string.Empty;
        try
        {
            var state = _stateReader();
            return state == null ? null : RenderRegion(region, state, _imageReader);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _lastReadFailure = $"{ex.GetType().Name}:0x{ex.HResult:X8}"; // No file paths, image data or desktop contents.
            if (ex is ArgumentException argument) _lastReadFailure += $":parameter={argument.ParamName}";
            _lastReadFailure += ":method=" + new System.Diagnostics.StackTrace(ex).GetFrame(0)?.GetMethod()?.Name;
            return null; // Existing themed background, never a screenshot fallback.
        }
    }

    public static BitmapSource? RenderRegion(Rect region, DesktopWallpaperState state, Func<string, DesktopWallpaperImage?> imageReader)
    {
        if (region.IsEmpty || !double.IsFinite(region.X) || !double.IsFinite(region.Y) || !double.IsFinite(region.Width) || !double.IsFinite(region.Height)
            || region.Width <= 0 || region.Height <= 0 || region.Width > 8192 || region.Height > 8192
            || region.Width * region.Height > 8_000_000) return null;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(state.Background), null, new Rect(0, 0, region.Width, region.Height));
            if (state.Enabled && state.Monitors.Count > 0)
            {
                Rect virtualDesktop = Rect.Empty;
                foreach (var monitor in state.Monitors) virtualDesktop.Union(monitor.Bounds);
                foreach (var monitor in state.Monitors)
                {
                    Rect intersection = Rect.Intersect(monitor.Bounds, region);
                    if (intersection.IsEmpty || intersection.Width <= 0 || intersection.Height <= 0) continue;
                    string path = state.Layout == DesktopWallpaperLayout.Span
                        ? state.Monitors.FirstOrDefault(m => !string.IsNullOrEmpty(m.ImagePath))?.ImagePath ?? ""
                        : monitor.ImagePath;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var image = imageReader(path);
                    if (image == null || image.NaturalSize.Width <= 0 || image.NaturalSize.Height <= 0) continue;
                    var localClip = new Rect(intersection.X - region.X, intersection.Y - region.Y, intersection.Width, intersection.Height);
                    drawing.PushClip(new RectangleGeometry(localClip));
                    if (state.Layout == DesktopWallpaperLayout.Tile)
                    {
                        // Absolute viewport phase is anchored at the desktop origin, including negative monitors.
                        var brush = new ImageBrush(image.Bitmap)
                        {
                            ViewportUnits = BrushMappingMode.Absolute,
                            Viewport = new Rect(-intersection.X, -intersection.Y, image.NaturalSize.Width, image.NaturalSize.Height),
                            TileMode = TileMode.Tile, Stretch = Stretch.Fill
                        };
                        drawing.DrawRectangle(brush, null, localClip);
                    }
                    else
                    {
                        Rect placement = GetPlacement(image.NaturalSize,
                            state.Layout == DesktopWallpaperLayout.Span ? virtualDesktop : monitor.Bounds, state.Layout);
                        placement.Offset(-region.X, -region.Y);
                        drawing.DrawImage(image.Bitmap, placement);
                    }
                    drawing.Pop();
                }
            }
        }
        var rendered = new RenderTargetBitmap((int)Math.Ceiling(region.Width), (int)Math.Ceiling(region.Height), 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return rendered;
    }

    public static Rect GetPlacement(Size image, Rect monitor, DesktopWallpaperLayout layout)
    {
        if (layout == DesktopWallpaperLayout.Stretch) return monitor;
        double scale = layout == DesktopWallpaperLayout.Center ? 1
            : layout == DesktopWallpaperLayout.Fit ? Math.Min(monitor.Width / image.Width, monitor.Height / image.Height)
            : Math.Max(monitor.Width / image.Width, monitor.Height / image.Height);
        double width = image.Width * scale, height = image.Height * scale;
        return new Rect(monitor.X + (monitor.Width - width) / 2, monitor.Y + (monitor.Height - height) / 2, width, height);
    }

    public void ClearCache() => _images.Clear();

    private DesktopWallpaperImage? LoadImage(string path)
    {
        // Windows normally returns a local wallpaper/cache path. Do not create network file requests.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(path)) return null;
        var file = new FileInfo(path);
        if (file.Length <= 0 || file.Length > 100_000_000) return null;
        if (_images.TryGetValue(path, out var cached) && cached.Changed == file.LastWriteTimeUtc && cached.Length == file.Length)
            return cached.Image;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        var natural = new Size(decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
        if (natural.Width <= 0 || natural.Height <= 0 || natural.Width > 32768 || natural.Height > 32768) return null;
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (natural.Width >= natural.Height) bitmap.DecodePixelWidth = (int)Math.Min(natural.Width, 4096);
        else bitmap.DecodePixelHeight = (int)Math.Min(natural.Height, 4096);
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        var image = new DesktopWallpaperImage(bitmap, natural);
        if (_images.Count >= 8) _images.Clear();
        _images[path] = (file.LastWriteTimeUtc, file.Length, image);
        return image;
    }

    public static DesktopWallpaperState? ReadWindowsWallpaper()
    {
        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"));
            if (type == null) return null;
            instance = Activator.CreateInstance(type);
            if (instance is not IDesktopWallpaper wallpaper) return null;
            Marshal.ThrowExceptionForHR(wallpaper.GetPosition(out var position));
            Marshal.ThrowExceptionForHR(wallpaper.GetBackgroundColor(out uint color));
            Marshal.ThrowExceptionForHR(wallpaper.GetMonitorDevicePathCount(out uint count));
            var monitors = new List<DesktopWallpaperMonitor>();
            for (uint i = 0; i < Math.Min(count, 32); i++)
            {
                if (wallpaper.GetMonitorDevicePathAt(i, out string id) < 0 || wallpaper.GetMonitorRECT(id, out var bounds) < 0) continue;
                if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) continue;
                string path = wallpaper.GetWallpaper(id, out string imagePath) >= 0 ? imagePath : string.Empty;
                monitors.Add(new(new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), path));
            }
            bool enabled = wallpaper.GetStatus(out uint status) < 0 || (status & 1) != 0;
            return new(position, Color.FromRgb((byte)(color & 255), (byte)((color >> 8) & 255), (byte)((color >> 16) & 255)), monitors, enabled);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (instance != null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
        }
    }

    public void Dispose() { _disposed = true; ClearCache(); }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        // Native vtable order; setter slots are declarations only and are never invoked.
        [PreserveSig] int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string path);
        [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int GetMonitorDevicePathAt(uint index, [MarshalAs(UnmanagedType.LPWStr)] out string monitorId);
        [PreserveSig] int GetMonitorDevicePathCount(out uint count);
        [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out RECT bounds);
        [PreserveSig] int SetBackgroundColor(uint color);
        [PreserveSig] int GetBackgroundColor(out uint color);
        [PreserveSig] int SetPosition(DesktopWallpaperLayout position);
        [PreserveSig] int GetPosition(out DesktopWallpaperLayout position);
        [PreserveSig] int SetSlideshow(IntPtr items);
        [PreserveSig] int GetSlideshow(out IntPtr items);
        [PreserveSig] int SetSlideshowOptions(uint options, uint tick);
        [PreserveSig] int GetSlideshowOptions(out uint options, out uint tick);
        [PreserveSig] int AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, uint direction);
        [PreserveSig] int GetStatus(out uint status);
        [PreserveSig] int Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }
}
