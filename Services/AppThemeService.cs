using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EverythingonStart.Models;
using Microsoft.Win32;

namespace EverythingonStart.Services;

public readonly record struct SystemThemeState(bool IsLightTheme, Color AccentColor);

public static class ThemeResourceKeys
{
    public const string AccentTextBrush = "Theme.AccentTextBrush";
    public const string SearchBackgroundBrush = "Theme.SearchBackgroundBrush";
}

public sealed class AppThemeService : IDisposable
{
    private const string ServiceResourceKey = "AppThemeService";
    private readonly AppSettingsService _settings;
    private readonly Func<SystemThemeState> _readSystemTheme;
    private readonly bool _observeSystemChanges;
    private readonly System.Windows.Application _application;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, Color> _darkColors;
    private readonly Dictionary<Window, HwndSource?> _windows = [];
    private bool _disposed;
    private int _refreshQueued;

    public ResourceDictionary Resources { get; }
    public event EventHandler? ThemeChanged;

    public static AppThemeService GetOrCreate(AppSettingsService settings)
    {
        var application = System.Windows.Application.Current
            ?? throw new InvalidOperationException("Theme resources require a WPF application.");
        if (application.Resources[ServiceResourceKey] is AppThemeService existing && !existing._disposed)
            return existing;
        return new AppThemeService(settings);
    }

    public AppThemeService(AppSettingsService settings, Func<SystemThemeState>? systemThemeReader = null, bool observeSystemChanges = true)
    {
        _settings = settings;
        _readSystemTheme = systemThemeReader ?? ReadSystemTheme;
        _observeSystemChanges = observeSystemChanges;
        _application = System.Windows.Application.Current
            ?? throw new InvalidOperationException("Theme resources require a WPF application.");
        _dispatcher = _application.Dispatcher;
        Resources = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/EverythingonStart;component/Themes/ThemeResources.xaml")
        };
        _darkColors = Resources.Keys.OfType<string>()
            .Where(key => Resources[key] is SolidColorBrush)
            .ToDictionary(key => key, key => ((SolidColorBrush)Resources[key]).Color);
        _application.Resources.MergedDictionaries.Add(Resources);
        _application.Resources[ServiceResourceKey] = this;
        ApplyCurrentSettings();
        if (_observeSystemChanges)
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void ApplyCurrentSettings()
    {
        _dispatcher.VerifyAccess();
        if (_disposed)
            return;

        var configured = _settings.Current.ThemeMode;
        var state = configured == AppThemeMode.System || _settings.Current.UseSystemAccentColor
            ? _readSystemTheme()
            : new SystemThemeState(false, Color.FromRgb(88, 182, 234));
        bool light = configured == AppThemeMode.Light || (configured == AppThemeMode.System && state.IsLightTheme);
        var colors = new Dictionary<string, Color>(_darkColors);
        void Set(string name, Color color) => colors[$"Theme.{name}Brush"] = color;
        if (light)
        {
            foreach (var (name, value) in LightColors)
                Set(name, (Color)ColorConverter.ConvertFromString(value));
        }

        Color accent = _settings.Current.UseSystemAccentColor ? state.AccentColor : Color.FromRgb(88, 182, 234);
        accent.A = 255;
        Color card = colors["Theme.CardBrush"];
        Set("Accent", accent);
        Set("AccentText", EnsureTextContrast(accent, light ? colors["Theme.SurfaceBrush"] : card));
        Set("AccentForeground", ContrastForeground(accent));
        Set("AccentTint", Blend(accent, card, 0.18));
        if (_settings.Current.UseSystemAccentColor)
        {
            Set("PrimaryButton", accent);
            Set("PrimaryHover", Blend(Colors.White, accent, 0.12));
            Set("PrimaryPressed", Blend(Colors.Black, accent, 0.12));
            Set("PrimaryBorder", accent);
            Set("PrimaryForeground", ContrastForeground(accent));
        }

        Color search = colors["Theme.SearchShadowBrush"];
        byte alpha = (byte)Math.Round(Math.Clamp(_settings.Current.WindowOpacityPercent, 75, 100) * 255 / 100);
        Set("SearchBackground", Color.FromArgb(alpha, search.R, search.G, search.B));
        foreach (byte opacity in new byte[] { 0x14, 0x1A, 0x1C, 0x1E, 0x1F, 0x22, 0x24, 0x26, 0x28, 0x2A, 0x2B, 0x2E, 0x33, 0x38, 0x44 })
        {
            byte channel = light ? (byte)0 : (byte)255;
            Set($"Alpha{opacity:X2}", Color.FromArgb(opacity, channel, channel, channel));
        }

        bool changed = false;
        foreach (var (key, color) in colors)
        {
            if (Resources[key] is SolidColorBrush old && old.Color == color)
                continue;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Resources[key] = brush;
            changed = true;
        }
        if (changed)
            ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RegisterWindow(Window window)
    {
        if (_disposed || !_observeSystemChanges || _windows.ContainsKey(window))
            return;
        _windows[window] = null;
        window.SourceInitialized += OnWindowSourceInitialized;
        window.Closed += OnWindowClosed;
        AttachSource(window);
    }

    private void AttachSource(Window window)
    {
        if (_windows[window] != null)
            return;
        var source = PresentationSource.FromVisual(window) as HwndSource;
        if (source == null)
        {
            // SourceInitialized can run before the root visual is connected.
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
                source = HwndSource.FromHwnd(hwnd);
        }
        if (source != null)
        {
            source.AddHook(HandleWindowMessage);
            _windows[window] = source;
        }
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
            AttachSource(window);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
            UnregisterWindow(window);
    }

    private void UnregisterWindow(Window window)
    {
        window.SourceInitialized -= OnWindowSourceInitialized;
        window.Closed -= OnWindowClosed;
        if (_windows.Remove(window, out var source) && source != null && !source.IsDisposed)
            source.RemoveHook(HandleWindowMessage);
    }

    private IntPtr HandleWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Windows broadcasts these when app mode or the DWM accent changes.
        if (msg is 0x001A or 0x031A or 0x0320)
            QueueSystemRefresh();
        return IntPtr.Zero;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => QueueSystemRefresh();

    private void QueueSystemRefresh()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished
            || (_settings.Current.ThemeMode != AppThemeMode.System && !_settings.Current.UseSystemAccentColor))
            return;
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_disposed)
                ApplyCurrentSettings();
        }));
    }

    private static SystemThemeState ReadSystemTheme()
    {
        bool light = true;
        Color accent = Color.FromRgb(88, 182, 234);
        try
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = personalize?.GetValue("AppsUseLightTheme") is not int value || value != 0;
            using var uiAccent = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (uiAccent?.GetValue("AccentColorMenu") is int menuColor)
            {
                accent = DecodeAccentColor(unchecked((uint)menuColor));
            }
            else if (dwm?.GetValue("AccentColor") is int raw)
            {
                accent = DecodeAccentColor(unchecked((uint)raw));
            }
            else if (DwmGetColorizationColor(out uint argb, out _) == 0)
            {
                accent = Color.FromRgb((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Restricted registry access must not prevent the launcher from opening.
        }
        return new SystemThemeState(light, accent);
    }

    // Windows' UI/DWM registry accent values are ABGR, not ARGB.
    private static Color DecodeAccentColor(uint abgr) => Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));

    private static readonly Dictionary<string, string> LightColors = new()
    {
        ["WindowBackground"] = "#F5F6F8", ["SearchShadow"] = "#FAFBFC",
        ["Surface"] = "#EDF0F4", ["Card"] = "#FFFFFF", ["Input"] = "#FFFFFF",
        ["Control"] = "#FFFFFF", ["Button"] = "#E8EDF2", ["Hover"] = "#DFE6EE",
        ["Pressed"] = "#CCD6E2", ["Border"] = "#CAD2DD", ["StrongBorder"] = "#8793A1",
        ["TextPrimary"] = "#18212F", ["TextSecondary"] = "#374151",
        ["TextMuted"] = "#526173", ["TextDisabled"] = "#6B7785"
    };

    private static Color Blend(Color foreground, Color background, double amount) => Color.FromRgb(
        (byte)Math.Round(foreground.R * amount + background.R * (1 - amount)),
        (byte)Math.Round(foreground.G * amount + background.G * (1 - amount)),
        (byte)Math.Round(foreground.B * amount + background.B * (1 - amount)));

    private static Color EnsureTextContrast(Color accent, Color background)
    {
        if (Contrast(accent, background) >= 4.5)
            return accent;
        var destination = ContrastForeground(background);
        for (int step = 1; step <= 100; step++)
        {
            var candidate = Blend(destination, accent, step / 100.0);
            if (Contrast(candidate, background) >= 4.5)
                return candidate;
        }
        return destination;
    }

    private static Color ContrastForeground(Color background) =>
        Contrast(Colors.Black, background) >= Contrast(Colors.White, background) ? Colors.Black : Colors.White;

    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value)
            {
                double s = value / 255.0;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
        double first = Luminance(a), second = Luminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_observeSystemChanges)
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        foreach (var window in _windows.Keys.ToArray())
            UnregisterWindow(window);
        _application.Resources.MergedDictionaries.Remove(Resources);
        if (ReferenceEquals(_application.Resources[ServiceResourceKey], this))
            _application.Resources.Remove(ServiceResourceKey);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out uint color, [MarshalAs(UnmanagedType.Bool)] out bool opaqueBlend);
}
