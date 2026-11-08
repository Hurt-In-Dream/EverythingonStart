using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace EverythingonStart.Services;

// Per-window DWM composition only. No bitmap acquisition, wallpaper files, GIFs or foreign-process hooks.
public sealed class WindowBackdropService : IDisposable
{
    private readonly Window _window;
    private readonly Border _surface;
    private readonly FrameworkElement? _shadow;
    private readonly AppSettingsService _settings;
    private readonly Func<IntPtr, bool, bool> _nativeApply;
    private readonly bool _useDefaultNativeApi;
    private readonly Func<bool> _highContrastReader;
    private readonly AppThemeService? _theme;
    private HwndSource? _source;
    private bool _disposed;
    private bool _lastEnabled;
    private bool _handoverCover;
    private string _nativeDescription = "未应用";
    public bool IsApplied { get; private set; }
    public string SourceDescription => IsApplied ? $"实时 {_nativeDescription} 背景模糊（无截图、无本地预览）" : $"原有主题背景（关闭或系统不可用：{_nativeDescription}）";

    public WindowBackdropService(Window window, Border surface, AppSettingsService settings,
        FrameworkElement? shadow = null, Func<IntPtr, bool, bool>? nativeApply = null, Func<bool>? highContrastReader = null)
    {
        _window = window; _surface = surface; _settings = settings; _shadow = shadow;
        _nativeApply = nativeApply ?? ApplyNativeBackdrop;
        _useDefaultNativeApi = nativeApply == null;
        _highContrastReader = highContrastReader ?? (() => SystemParameters.HighContrast);
        _theme = Application.Current?.Resources["AppThemeService"] as AppThemeService;
        if (_theme != null) _theme.ThemeChanged += OnThemeChanged;
        window.SourceInitialized += OnSourceInitialized;
        window.IsVisibleChanged += OnVisibilityChanged;
        surface.SizeChanged += OnSurfaceSizeChanged;
        surface.Loaded += OnSurfaceLoaded;
        window.Closed += OnClosed;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        AttachSource();
        ApplyCurrentSettings();
    }

    private void AttachSource()
    {
        if (_source != null || _disposed) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(HandleWindowMessage);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        AttachSource();
        ApplyCurrentSettings();
    }

    public void ApplyCurrentSettings()
    {
        _window.Dispatcher.VerifyAccess();
        if (_disposed) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        bool enabled = _settings.Current.EnableGaussianBlur && !_highContrastReader() && !_window.AllowsTransparency;
        if (_source?.CompositionTarget != null)
            _source.CompositionTarget.BackgroundColor = Colors.Transparent;
        bool applied = hwnd != IntPtr.Zero && (_useDefaultNativeApi
            ? ApplyNativeBackdropCore(hwnd, enabled, out _nativeDescription)
            : _nativeApply(hwnd, enabled));
        IsApplied = enabled && applied;
        _lastEnabled = enabled;
        UpdateSurface();
        UpdateWindowRegion();
    }

    public void SetHandoverCover(bool enabled)
    {
        _window.Dispatcher.VerifyAccess();
        if (_disposed || _handoverCover == enabled) return;
        _handoverCover = enabled;
        UpdateSurface();
    }

    private void UpdateSurface()
    {
        if (_shadow != null) _shadow.Visibility = IsApplied ? Visibility.Collapsed : Visibility.Visible;
        string key = _window is MainWindow ? "Theme.SearchBackgroundBrush" : "Theme.WindowBackgroundBrush";
        if ((IsApplied || _handoverCover) && Application.Current?.TryFindResource(key) is SolidColorBrush brush)
        {
            var color = brush.Color;
            color.A = _handoverCover ? (byte)255 :
                (byte)Math.Round(Math.Clamp(_settings.Current.WindowOpacityPercent, 75, 100) * 255 / 100);
            var tint = new SolidColorBrush(color); tint.Freeze(); _surface.Background = tint;
        }
        else _surface.SetResourceReference(Border.BackgroundProperty, key);
    }

    public void PrepareForShow() => ApplyCurrentSettings();

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => UpdateWindowRegion();
    private void OnSurfaceLoaded(object sender, RoutedEventArgs e) => UpdateWindowRegion();

    private void UpdateWindowRegion()
    {
        if (_disposed) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (_surface.ActualWidth <= 0 || _surface.ActualHeight <= 0 || !GetWindowRect(hwnd, out var bounds)) return;
        // Keep the card region in every mode, including disabled/high-contrast/unavailable fallback.
        // Non-layered WPF shadow margins otherwise expose the native black/white window frame.
        var topLeft = _surface.PointToScreen(new Point());
        var bottomRight = _surface.PointToScreen(new Point(_surface.ActualWidth, _surface.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(_surface);
        int diameter = (int)Math.Round(_surface.CornerRadius.TopLeft * 2 * dpi.DpiScaleX);
        var region = CreateRoundRectRgn((int)Math.Round(topLeft.X - bounds.Left), (int)Math.Round(topLeft.Y - bounds.Top),
            (int)Math.Round(bottomRight.X - bounds.Left) + 1, (int)Math.Round(bottomRight.Y - bounds.Top) + 1, diameter, diameter);
        if (region != IntPtr.Zero && SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_window.IsVisible) ApplyCurrentSettings();
    }

    private IntPtr HandleWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_disposed && message == 0x0084 && _window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip
            && _surface.IsLoaded && _surface.ActualWidth > 0 && _surface.ActualHeight > 0)
        {
            // Native frame edges lie outside the clipped card. Preserve resize handles inside its edges.
            long coordinates = lParam.ToInt64();
            var pointer = new Point((short)(coordinates & 0xffff), (short)((coordinates >> 16) & 0xffff));
            var topLeft = _surface.PointToScreen(new Point());
            var bottomRight = _surface.PointToScreen(new Point(_surface.ActualWidth, _surface.ActualHeight));
            var bounds = new Rect(topLeft, bottomRight);
            if (bounds.Contains(pointer))
            {
                var dpi = VisualTreeHelper.GetDpi(_surface);
                bool left = pointer.X < topLeft.X + 7 * dpi.DpiScaleX;
                bool right = pointer.X >= bottomRight.X - 7 * dpi.DpiScaleX;
                bool top = pointer.Y < topLeft.Y + 7 * dpi.DpiScaleY;
                bool bottom = pointer.Y >= bottomRight.Y - 7 * dpi.DpiScaleY;
                int hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 0;
                if (hit != 0) { handled = true; return new IntPtr(hit); }
            }
        }
        if (!_disposed && message is 0x001A or 0x031A or 0x031E or 0x02E0) // settings/theme/DWM/DPI changes
            _window.Dispatcher.InvokeAsync(ApplyCurrentSettings);
        return IntPtr.Zero;
    }
    private void OnThemeChanged(object? sender, EventArgs e) => ApplyCurrentSettings();
    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) _window.Dispatcher.InvokeAsync(ApplyCurrentSettings);
    }
    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public static bool ApplyNativeBackdrop(IntPtr hwnd, bool enabled)
        => ApplyNativeBackdropCore(hwnd, enabled, out _);

    // The popup owns its WPF animation. A second native HWND show/fade transition can
    // leave even an opaque, rendered cover translucent when focus leaves Start.
    public static bool DisableNativeTransitions(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            int disabled = 1; // DWMWA_TRANSITIONS_FORCEDISABLED, per-window only.
            return DwmSetWindowAttribute(hwnd, 3, ref disabled, sizeof(int)) >= 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }

    private static bool ApplyNativeBackdropCore(IntPtr hwnd, bool enabled, out string description)
    {
        description = "DWM 不可用";
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            if (DwmIsCompositionEnabled(out bool composition) < 0 || !composition) return false;
            int nonClient = 1; // DWMNCRP_DISABLED: WPF owns the entire borderless card.
            DwmSetWindowAttribute(hwnd, 2, ref nonClient, sizeof(int));
            // A composited non-layered WPF window exposes client alpha to DWM without capturing any pixels.
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) < 0) return false;
            int corner = 1; DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int)); // rounded by our card region, not the outer HWND
            int borderColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            DwmSetWindowAttribute(hwnd, 34, ref borderColor, sizeof(int));
            int none = 1; DwmSetWindowAttribute(hwnd, 38, ref none, sizeof(int));
            // Accent bit 4 makes the compositor respect the GDI region (also documented by DWMBlurGlass).
            // Flag 2 adds a second, near-opaque native tint. With the default 95% WPF
            // tint this almost completely hides the live background. Keep only region clipping.
            var accent = new ACCENT_POLICY { State = enabled ? 4 : 0, Flags = enabled ? (1 << 4) : 0,
                Color = enabled ? 0x01000000u : 0u };
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>());
            try
            {
                Marshal.StructureToPtr(accent, memory, false);
                var data = new WINDOWCOMPOSITIONATTRIBDATA { Attribute = 19, Data = memory, Size = Marshal.SizeOf<ACCENT_POLICY>() };
                if (SetWindowCompositionAttribute(hwnd, ref data))
                {
                    description = enabled ? "Accent Acrylic（单层染色）" : "关闭";
                    return true;
                }
                description = $"Accent 失败 ({Marshal.GetLastWin32Error()})";
            }
            finally { Marshal.FreeHGlobal(memory); }
            // Public SystemBackdrop fallback, as used by DWMBlurGlass's DWMAPI path on Windows 11 22H2+.
            int material = enabled ? 3 : 1;
            int result = DwmSetWindowAttribute(hwnd, 38, ref material, sizeof(int));
            description = result >= 0 ? "SystemBackdrop Acrylic" : $"{description}; SystemBackdrop HRESULT=0x{result:X8}";
            return result >= 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd != IntPtr.Zero && _lastEnabled) _nativeApply(hwnd, false);
        _disposed = true;
        if (_theme != null) _theme.ThemeChanged -= OnThemeChanged;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _surface.SizeChanged -= OnSurfaceSizeChanged;
        _surface.Loaded -= OnSurfaceLoaded;
        _window.Closed -= OnClosed;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        if (_source is { IsDisposed: false }) _source.RemoveHook(HandleWindowMessage);
        _source = null; IsApplied = false;
    }

    [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct ACCENT_POLICY { public int State, Flags; public uint Color; public int Animation; }
    [StructLayout(LayoutKind.Sequential)] private struct WINDOWCOMPOSITIONATTRIBDATA { public int Attribute; public IntPtr Data; public int Size; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);
}
