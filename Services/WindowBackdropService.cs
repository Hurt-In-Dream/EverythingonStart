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
    private readonly Func<bool> _highContrastReader;
    private readonly AppThemeService? _theme;
    private HwndSource? _source;
    private bool _disposed;
    private bool _lastEnabled;
    public bool IsApplied { get; private set; }
    public string SourceDescription => IsApplied ? "实时 DWM/Accent 亚克力合成（无截图、无本地预览）" : "原有主题背景（关闭或系统不可用）";

    public WindowBackdropService(Window window, Border surface, AppSettingsService settings,
        FrameworkElement? shadow = null, Func<IntPtr, bool, bool>? nativeApply = null, Func<bool>? highContrastReader = null)
    {
        _window = window; _surface = surface; _settings = settings; _shadow = shadow;
        _nativeApply = nativeApply ?? ApplyNativeBackdrop;
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
        bool applied = hwnd != IntPtr.Zero && _nativeApply(hwnd, enabled);
        IsApplied = enabled && applied;
        _lastEnabled = enabled;
        if (_shadow != null) _shadow.Visibility = IsApplied ? Visibility.Collapsed : Visibility.Visible;
        string key = _window is MainWindow ? "Theme.SearchBackgroundBrush" : "Theme.WindowBackgroundBrush";
        if (IsApplied && Application.Current?.TryFindResource(key) is SolidColorBrush brush)
        {
            var color = brush.Color;
            color.A = (byte)Math.Round(Math.Clamp(_settings.Current.WindowOpacityPercent, 75, 100) * 255 / 100);
            var tint = new SolidColorBrush(color); tint.Freeze(); _surface.Background = tint;
        }
        else _surface.SetResourceReference(Border.BackgroundProperty, key);
        UpdateWindowRegion();
    }

    public void PrepareForShow() => ApplyCurrentSettings();

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => UpdateWindowRegion();
    private void OnSurfaceLoaded(object sender, RoutedEventArgs e) => UpdateWindowRegion();

    private void UpdateWindowRegion()
    {
        if (_disposed) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!IsApplied) { SetWindowRgn(hwnd, IntPtr.Zero, true); return; }
        if (_surface.ActualWidth <= 0 || _surface.ActualHeight <= 0 || !GetWindowRect(hwnd, out var bounds)) return;
        // Clip native acrylic to the actual rounded card, not its transparent WPF shadow margins.
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
        if (!_disposed && message is 0x001A or 0x031A or 0x031E) // settings/theme/DWM composition changes
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
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            if (DwmIsCompositionEnabled(out bool composition) < 0 || !composition) return false;
            // A composited non-layered WPF window exposes client alpha to DWM without capturing any pixels.
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) < 0) return false;
            int corner = 2; DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
            int none = 1; DwmSetWindowAttribute(hwnd, 38, ref none, sizeof(int));
            var accent = new ACCENT_POLICY { State = enabled ? 4 : 0, Flags = 2, Color = enabled ? 0x01000000u : 0u };
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>());
            try
            {
                Marshal.StructureToPtr(accent, memory, false);
                var data = new WINDOWCOMPOSITIONATTRIBDATA { Attribute = 19, Data = memory, Size = Marshal.SizeOf<ACCENT_POLICY>() };
                if (SetWindowCompositionAttribute(hwnd, ref data)) return true;
            }
            finally { Marshal.FreeHGlobal(memory); }
            // Public SystemBackdrop fallback, as used by DWMBlurGlass's DWMAPI path on Windows 11 22H2+.
            int material = enabled ? 3 : 1;
            return DwmSetWindowAttribute(hwnd, 38, ref material, sizeof(int)) >= 0;
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
    [DllImport("user32.dll")] private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);
}
