using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EverythingonStart.Controls;

namespace EverythingonStart.Services;

public sealed class WindowBackdropService : IDisposable
{
    private readonly Window _window;
    private readonly GaussianBlurBackdrop _backdrop;
    private readonly AppSettingsService _settings;
    private readonly Func<Rect, BitmapSource?> _wallpaperReader;
    private readonly DesktopWallpaperService _wallpaperService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Func<bool> _highContrastReader;
    private HwndSource? _source;
    private bool _preparedForShow;
    private bool _disposed;
    private Rect _lastRegion = Rect.Empty;
    public string SourceDescription => "Windows 桌面壁纸图层（非屏幕或窗口截图）";

    public WindowBackdropService(Window window, GaussianBlurBackdrop backdrop, AppSettingsService settings,
        Func<Rect, BitmapSource?>? wallpaperReader = null, Func<bool>? highContrastReader = null)
    {
        _window = window;
        _backdrop = backdrop;
        _settings = settings;
        _wallpaperService = new DesktopWallpaperService();
        _wallpaperReader = wallpaperReader ?? _wallpaperService.ReadRegion;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); if (_window.IsVisible) TryLoadWallpaper(new WindowInteropHelper(_window).Handle, force: true); };
        _highContrastReader = highContrastReader ?? (() => SystemParameters.HighContrast);
        window.SourceInitialized += OnSourceInitialized;
        window.IsVisibleChanged += OnVisibilityChanged;
        window.Closed += OnClosed;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        AttachSource();
        ApplyCurrentSettings();
    }

    public void ApplyCurrentSettings()
    {
        ApplyVisualState(refreshIfNeeded: true);
    }

    private void ApplyVisualState(bool refreshIfNeeded)
    {
        _window.Dispatcher.VerifyAccess();
        if (_disposed)
            return;
        _backdrop.BlurRadius = Math.Clamp(_settings.Current.GaussianBlurRadius, 6, 40);
        double opacity = _settings.Current.WindowOpacityPercent;
        _backdrop.TintOpacity = Math.Clamp(double.IsFinite(opacity) ? opacity : 95, 75, 100) / 100;
        if (!_settings.Current.EnableGaussianBlur || _highContrastReader())
        {
            _backdrop.ClearSnapshot();
            _wallpaperService.ClearCache();
            _refreshTimer.Stop();
            _preparedForShow = false;
            _lastRegion = Rect.Empty;
        }
        else if (_backdrop.Snapshot == null)
        {
            _backdrop.Visibility = Visibility.Hidden; // Measure before loading wallpaper without drawing an empty tint.
            if (refreshIfNeeded) QueueRefresh();
        }
    }

    public void PrepareForShow()
    {
        ApplyCurrentSettings();
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd != IntPtr.Zero)
            TryLoadWallpaper(hwnd, force: _window.IsVisible);
        // The source is a wallpaper file/layer; visibility never contaminates it with other windows.
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => AttachSource();

    private void AttachSource()
    {
        if (_source != null || _disposed)
            return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(HandleWindowMessage);
    }

    private IntPtr HandleWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed)
            return IntPtr.Zero;
        if (message == 0x0018 && wParam != IntPtr.Zero && !IsWindowVisible(hwnd)) // WM_SHOWWINDOW, before display
            TryLoadWallpaper(hwnd);
        else if (message == 0x0046 && lParam != IntPtr.Zero && !IsWindowVisible(hwnd)) // WM_WINDOWPOSCHANGING
        {
            var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((position.Flags & 0x0040) != 0) // SWP_SHOWWINDOW: use the final centered/moved bounds.
                TryLoadWallpaper(hwnd, position);
        }
        else if (message is 0x0047 or 0x02E0 && _window.IsVisible) // movement/resize/DPI: coalesced, not continuous capture
            QueueRefresh();
        else if (message is 0x001A or 0x007E) // wallpaper/display preference changed
        {
            _wallpaperService.ClearCache(); _lastRegion = Rect.Empty; QueueRefresh();
        }
        return IntPtr.Zero;
    }

    private void TryLoadWallpaper(IntPtr hwnd, WINDOWPOS? position = null, bool force = false)
    {
        if (_disposed || (_preparedForShow && !force) || !_settings.Current.EnableGaussianBlur || _highContrastReader())
            return;
        ApplyVisualState(refreshIfNeeded: false);
        _window.UpdateLayout();
        if (!GetWindowRect(hwnd, out var bounds) || _source?.CompositionTarget == null
            || _backdrop.ActualWidth <= 0 || _backdrop.ActualHeight <= 0)
        {
            _backdrop.ClearSnapshot();
            return;
        }
        if (position is { } pending && (pending.Flags & 0x0002) == 0) // not SWP_NOMOVE
        {
            bounds.Left = pending.X;
            bounds.Top = pending.Y;
        }
        var offset = _backdrop.TranslatePoint(new Point(), _window);
        var device = _source.CompositionTarget.TransformToDevice;
        var region = new Rect(Math.Round(bounds.Left + offset.X * device.M11),
            Math.Round(bounds.Top + offset.Y * device.M22),
            Math.Ceiling(_backdrop.ActualWidth * device.M11), Math.Ceiling(_backdrop.ActualHeight * device.M22));
        _preparedForShow = true;
        if (force && region == _lastRegion && _backdrop.Snapshot != null) return;
        _lastRegion = region;
        try
        {
            var snapshot = _wallpaperReader(region);
            if (snapshot != null)
                _backdrop.SetSnapshot(snapshot);
            else
                _backdrop.ClearSnapshot();
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or ArgumentException or System.IO.IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Unavailable wallpaper falls back to the theme. Never fall back to reading screen pixels.
            _backdrop.ClearSnapshot();
        }
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            _backdrop.ClearSnapshot();
            _wallpaperService.ClearCache();
            _refreshTimer.Stop();
            _lastRegion = Rect.Empty;
            _preparedForShow = false;
        }
        else QueueRefresh();
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            _window.Dispatcher.InvokeAsync(ApplyCurrentSettings);
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    private void QueueRefresh()
    {
        if (!_disposed && _window.IsVisible && _settings.Current.EnableGaussianBlur && !_highContrastReader() && !_refreshTimer.IsEnabled)
            _refreshTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _refreshTimer.Stop();
        _wallpaperService.Dispose();
        _window.SourceInitialized -= OnSourceInitialized;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.Closed -= OnClosed;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        if (_source is { IsDisposed: false })
            _source.RemoveHook(HandleWindowMessage);
        _source = null;
        _backdrop.ClearSnapshot();
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WINDOWPOS
    {
        public IntPtr Hwnd, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
