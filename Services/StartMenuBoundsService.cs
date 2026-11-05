using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace EverythingonStart.Services;

public readonly record struct StartMenuPlacement(
    double WindowLeftDip,
    double WindowTopDip,
    double WindowWidthDip,
    double WindowHeightDip,
    double CardWidthDip,
    double CardHeightDip,
    double BottomGapDip,
    double UiScaleFactor,
    string SourceDescription
);

public sealed class StartMenuBoundsService
{
    public const double ReferenceCardWidthDip = 834.0;
    public const double ReferenceCardHeightDip = 866.0;
    public const double ShadowMarginDip = 10.0;

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private static readonly Guid ClsidCUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");

    private readonly AppSettingsService _settingsService;
    private readonly object _syncLock = new();

    public string LastPlacementSource { get; private set; } = "自适应推导";

    public StartMenuBoundsService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public bool TryCaptureOpenStartMenuBounds(IntPtr candidateForegroundHwnd = default)
    {
        double screenWidthDip = SystemParameters.PrimaryScreenWidth;
        double screenHeightDip = SystemParameters.PrimaryScreenHeight;
        var workAreaDip = SystemParameters.WorkArea;
        if (screenWidthDip <= 0 || screenHeightDip <= 0)
            return false;

        double taskbarHeightDip = GetTaskbarHeightDip(out int taskbarTopPx);
        double usableBottomDip = Math.Min(workAreaDip.Bottom, screenHeightDip - taskbarHeightDip);
        double usableHeightDip = Math.Max(400.0, usableBottomDip - workAreaDip.Top);

        IntPtr[] candidates =
        [
            candidateForegroundHwnd,
            FindWindowW("Windows.UI.Core.CoreWindow", "\u5F00\u59CB"),
            FindWindowW("Windows.UI.Core.CoreWindow", "Start"),
            FindWindowW("Windows.UI.Core.CoreWindow", "\u641C\u7D22"),
            FindWindowW("Windows.UI.Core.CoreWindow", "Search")
        ];

        foreach (var hwnd in candidates)
        {
            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
                continue;

            uint dpi = GetDpiForWindow(hwnd);
            double dpiScale = dpi > 0 ? dpi / 96.0 : 1.0;

            if (TryExtractFlyoutRectPx(hwnd, screenWidthDip * dpiScale, usableHeightDip * dpiScale, out var cardRectPx))
            {
                double cardWidthDip = Math.Round((cardRectPx.Right - cardRectPx.Left) / dpiScale);
                double cardHeightDip = Math.Round((cardRectPx.Bottom - cardRectPx.Top) / dpiScale);

                if (cardWidthDip is >= 480.0 and <= 1200.0 && cardHeightDip is >= 480.0 and <= 1200.0)
                {
                    double bottomGapDip = 10.0;
                    if (taskbarTopPx > cardRectPx.Bottom)
                    {
                        double measuredGap = Math.Round((taskbarTopPx - cardRectPx.Bottom) / dpiScale);
                        if (measuredGap is >= 0.0 and <= 32.0)
                            bottomGapDip = measuredGap;
                    }

                    SaveCapturedBounds(cardWidthDip, cardHeightDip, bottomGapDip, screenWidthDip, screenHeightDip);
                    return true;
                }
            }
        }

        return false;
    }

    private void SaveCapturedBounds(
        double cardWidthDip,
        double cardHeightDip,
        double bottomGapDip,
        double screenWidthDip,
        double screenHeightDip
    )
    {
        lock (_syncLock)
        {
            var cur = _settingsService.Current;
            bool changed = Math.Abs(cur.CachedStartMenuCardWidthDip - cardWidthDip) >= 2.0
                           || Math.Abs(cur.CachedStartMenuCardHeightDip - cardHeightDip) >= 2.0
                           || Math.Abs(cur.CachedStartMenuBottomGapDip - bottomGapDip) >= 1.0
                           || Math.Abs(cur.CachedScreenWidthDip - screenWidthDip) >= 1.0
                           || Math.Abs(cur.CachedScreenHeightDip - screenHeightDip) >= 1.0;

            if (changed)
            {
                cur.CachedStartMenuCardWidthDip = cardWidthDip;
                cur.CachedStartMenuCardHeightDip = cardHeightDip;
                cur.CachedStartMenuBottomGapDip = bottomGapDip;
                cur.CachedScreenWidthDip = screenWidthDip;
                cur.CachedScreenHeightDip = screenHeightDip;
                _settingsService.Save();
            }
        }
    }

    private static bool TryExtractFlyoutRectPx(
        IntPtr hwnd,
        double maxScreenWidthPx,
        double maxUsableHeightPx,
        out RECT flyoutRectPx
    )
    {
        flyoutRectPx = default;

        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var dwmRect, Marshal.SizeOf<RECT>()) != 0
            || (dwmRect.Right - dwmRect.Left) <= 0)
        {
            if (!GetWindowRect(hwnd, out dwmRect))
                return false;
        }

        int w = dwmRect.Right - dwmRect.Left;
        int h = dwmRect.Bottom - dwmRect.Top;
        if (w <= 0 || h <= 0)
            return false;

        // 若 CoreWindow 自身即为非全屏浮层尺寸，直接使用其边界
        if (w >= 450 && w <= maxScreenWidthPx * 0.78 && h >= 450 && h <= maxUsableHeightPx - 20)
        {
            flyoutRectPx = dwmRect;
            return true;
        }

        // 若 CoreWindow 为全工作区透明宿主容器，通过 IUIAutomation 查找内部真实开始菜单可视面板边界
        return TryGetInnerCardBoundsViaUia(hwnd, maxScreenWidthPx, maxUsableHeightPx, out flyoutRectPx);
    }

    private static bool TryGetInnerCardBoundsViaUia(
        IntPtr hwnd,
        double maxScreenWidthPx,
        double maxUsableHeightPx,
        out RECT bestRectPx
    )
    {
        bestRectPx = default;
        var uiaType = Type.GetTypeFromCLSID(ClsidCUIAutomation);
        if (uiaType == null)
            return false;

        if (Activator.CreateInstance(uiaType) is not IUIAutomation uia)
            return false;

        if (uia.ElementFromHandle(hwnd, out var root) != 0 || root == null)
            return false;

        if (uia.get_ControlViewWalker(out var walker) != 0 || walker == null)
            return false;

        int inspected = 0;
        return SearchUiaSubtree(walker, root, depth: 0, ref inspected, maxScreenWidthPx, maxUsableHeightPx, ref bestRectPx);
    }

    private static bool SearchUiaSubtree(
        IUIAutomationTreeWalker walker,
        IUIAutomationElement parent,
        int depth,
        ref int inspected,
        double maxScreenWidthPx,
        double maxUsableHeightPx,
        ref RECT bestRectPx
    )
    {
        if (depth > 3 || inspected > 28)
            return bestRectPx.Right > bestRectPx.Left;

        if (walker.GetFirstChildElement(parent, out var child) != 0 || child == null)
            return bestRectPx.Right > bestRectPx.Left;

        while (child != null && inspected <= 28)
        {
            inspected++;
            if (child.get_CurrentBoundingRectangle(out var rect) == 0)
            {
                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;

                if (w >= 450 && w <= maxScreenWidthPx * 0.78 && h >= 450 && h <= maxUsableHeightPx - 16)
                {
                    int currentBestArea = (bestRectPx.Right - bestRectPx.Left) * (bestRectPx.Bottom - bestRectPx.Top);
                    if (w * h > currentBestArea)
                    {
                        bestRectPx = rect;
                    }
                }
            }

            if (depth < 3)
            {
                SearchUiaSubtree(walker, child, depth + 1, ref inspected, maxScreenWidthPx, maxUsableHeightPx, ref bestRectPx);
            }

            if (walker.GetNextSiblingElement(child, out var next) != 0)
                break;
            child = next;
        }

        return bestRectPx.Right > bestRectPx.Left;
    }

    public StartMenuPlacement CalculatePlacement()
    {
        double screenWidthDip = SystemParameters.PrimaryScreenWidth;
        double screenHeightDip = SystemParameters.PrimaryScreenHeight;
        var workAreaDip = SystemParameters.WorkArea;
        double taskbarHeightDip = GetTaskbarHeightDip(out _);

        return CalculatePlacementForDisplay(screenWidthDip, screenHeightDip, workAreaDip, taskbarHeightDip);
    }

    public StartMenuPlacement CalculatePlacementForDisplay(
        double screenWidthDip,
        double screenHeightDip,
        Rect workAreaDip,
        double taskbarHeightDip
    )
    {
        double usableBottomDip = Math.Min(workAreaDip.Bottom, screenHeightDip - taskbarHeightDip);
        double usableHeightDip = Math.Max(480.0, usableBottomDip - workAreaDip.Top);

        double cardWidthDip;
        double cardHeightDip;
        double bottomGapDip;
        string source;

        var cur = _settingsService.Current;
        bool hasValidCache = cur.CachedStartMenuCardWidthDip is >= 480.0 and <= 1200.0
                             && cur.CachedStartMenuCardHeightDip is >= 480.0 and <= 1200.0
                             && Math.Abs(cur.CachedScreenWidthDip - screenWidthDip) < 2.0
                             && Math.Abs(cur.CachedScreenHeightDip - screenHeightDip) < 2.0;

        if (hasValidCache)
        {
            cardWidthDip = cur.CachedStartMenuCardWidthDip;
            cardHeightDip = cur.CachedStartMenuCardHeightDip;
            bottomGapDip = Math.Clamp(cur.CachedStartMenuBottomGapDip, 0.0, 28.0);
            source = $"已识别开始菜单尺寸 ({cardWidthDip:F0}×{cardHeightDip:F0})";
        }
        else
        {
            (cardWidthDip, cardHeightDip, bottomGapDip) = EstimateStartMenuCardSize(screenWidthDip, usableHeightDip);
            source = $"屏幕自适应 ({cardWidthDip:F0}×{cardHeightDip:F0})";
        }

        double maxCardHeightDip = Math.Max(460.0, usableHeightDip - bottomGapDip - 12.0);
        double maxCardWidthDip = Math.Max(480.0, workAreaDip.Width - 24.0);
        cardHeightDip = Math.Min(cardHeightDip, maxCardHeightDip);
        cardWidthDip = Math.Min(cardWidthDip, maxCardWidthDip);

        double windowWidthDip = cardWidthDip + (ShadowMarginDip * 2.0);
        double windowHeightDip = cardHeightDip + (ShadowMarginDip * 2.0);

        bool centerAligned = IsTaskbarCentered();
        double windowLeftDip = centerAligned
            ? Math.Round((screenWidthDip - windowWidthDip) / 2.0)
            : Math.Round(workAreaDip.Left + 12.0 - ShadowMarginDip);

        // RootCardBorder 底部自带 ShadowMarginDip (10 DIP)，使可视卡片底边距离任务栏顶边精确为 bottomGapDip
        double windowTopDip = Math.Round(
            Math.Max(workAreaDip.Top + 2.0, usableBottomDip - bottomGapDip - cardHeightDip - ShadowMarginDip)
        );

        double uiScale = Math.Clamp(
            Math.Min(cardWidthDip / ReferenceCardWidthDip, cardHeightDip / ReferenceCardHeightDip),
            0.75,
            1.25
        );

        LastPlacementSource = source;
        return new StartMenuPlacement(
            WindowLeftDip: windowLeftDip,
            WindowTopDip: windowTopDip,
            WindowWidthDip: windowWidthDip,
            WindowHeightDip: windowHeightDip,
            CardWidthDip: cardWidthDip,
            CardHeightDip: cardHeightDip,
            BottomGapDip: bottomGapDip,
            UiScaleFactor: uiScale,
            SourceDescription: source
        );
    }

    public static (double CardWidthDip, double CardHeightDip, double BottomGapDip) EstimateStartMenuCardSize(
        double screenWidthDip,
        double usableHeightDip
    )
    {
        // 原生开始菜单在工作区中的标准占比（基准：1392 可用高度下高 866 DIP、宽 834 DIP、底部间距 10 DIP）
        const double heightToWorkAreaRatio = ReferenceCardHeightDip / 1392.0;
        const double widthToHeightAspect = ReferenceCardWidthDip / ReferenceCardHeightDip;

        double targetHeight = Math.Round(usableHeightDip * heightToWorkAreaRatio);
        targetHeight = Math.Clamp(targetHeight, 620.0, 960.0);

        double targetWidth = Math.Round(targetHeight * widthToHeightAspect);
        targetWidth = Math.Clamp(targetWidth, 600.0, Math.Min(920.0, screenWidthDip * 0.65));

        return (targetWidth, targetHeight, 10.0);
    }

    public static double GetTaskbarHeightDip(out int taskbarTopPx)
    {
        taskbarTopPx = 0;
        double taskbarHeightDip = 48.0;
        var taskbarHwnd = FindWindowW("Shell_TrayWnd", null);
        if (taskbarHwnd != IntPtr.Zero && GetWindowRect(taskbarHwnd, out var tbRect))
        {
            taskbarTopPx = tbRect.Top;
            uint dpi = GetDpiForWindow(taskbarHwnd);
            double dpiScale = dpi > 0 ? dpi / 96.0 : 1.0;
            int rawHeight = tbRect.Bottom - tbRect.Top;
            int rawWidth = tbRect.Right - tbRect.Left;
            if (rawWidth >= rawHeight && rawHeight > 0)
            {
                taskbarHeightDip = Math.Clamp(rawHeight / dpiScale, 32.0, 96.0);
            }
        }

        return taskbarHeightDip;
    }

    private static bool IsTaskbarCentered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        var val = key?.GetValue("TaskbarAl");
        return val is int intVal ? intVal != 0 : true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [ComImport]
    [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        [PreserveSig]
        int CompareElements(IUIAutomationElement el1, IUIAutomationElement el2, out int areSame);

        [PreserveSig]
        int CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, out int areSame);

        [PreserveSig]
        int GetRootElement(out IUIAutomationElement root);

        [PreserveSig]
        int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement? element);

        [PreserveSig]
        int ElementFromPoint(long pt, out IUIAutomationElement? element);

        [PreserveSig]
        int GetFocusedElement(out IUIAutomationElement? element);

        [PreserveSig]
        int GetRootElementBuildCache(IntPtr cacheRequest, out IUIAutomationElement? root);

        [PreserveSig]
        int ElementFromHandleBuildCache(IntPtr hwnd, IntPtr cacheRequest, out IUIAutomationElement? element);

        [PreserveSig]
        int ElementFromPointBuildCache(long pt, IntPtr cacheRequest, out IUIAutomationElement? element);

        [PreserveSig]
        int GetFocusedElementBuildCache(IntPtr cacheRequest, out IUIAutomationElement? element);

        [PreserveSig]
        int CreateTreeWalker(IntPtr pCondition, out IUIAutomationTreeWalker? walker);

        [PreserveSig]
        int get_ControlViewWalker(out IUIAutomationTreeWalker? walker);
    }

    [ComImport]
    [Guid("4042C624-389C-4AFC-A630-9DF854A541FC")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTreeWalker
    {
        [PreserveSig]
        int GetParentElement(IUIAutomationElement element, out IUIAutomationElement? parent);

        [PreserveSig]
        int GetFirstChildElement(IUIAutomationElement element, out IUIAutomationElement? first);

        [PreserveSig]
        int GetLastChildElement(IUIAutomationElement element, out IUIAutomationElement? last);

        [PreserveSig]
        int GetNextSiblingElement(IUIAutomationElement element, out IUIAutomationElement? next);
    }

    [ComImport]
    [Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId(out IntPtr runtimeId);
        [PreserveSig] int FindFirst(int scope, IntPtr condition, out IUIAutomationElement? found);
        [PreserveSig] int FindAll(int scope, IntPtr condition, out IntPtr found);
        [PreserveSig] int FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IUIAutomationElement? found);
        [PreserveSig] int FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
        [PreserveSig] int BuildUpdatedCache(IntPtr cacheRequest, out IUIAutomationElement? updatedElement);
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, out object value);
        [PreserveSig] int GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue, out object value);
        [PreserveSig] int GetCachedPropertyValue(int propertyId, out object value);
        [PreserveSig] int GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue, out object value);
        [PreserveSig] int GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        [PreserveSig] int GetCachedPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        [PreserveSig] int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object patternObject);
        [PreserveSig] int GetCachedPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object patternObject);
        [PreserveSig] int GetCachedParent(out IUIAutomationElement? parent);
        [PreserveSig] int GetCachedChildren(out IntPtr children);
        [PreserveSig] int get_CurrentProcessId(out int retVal);
        [PreserveSig] int get_CurrentControlType(out int retVal);
        [PreserveSig] int get_CurrentLocalizedControlType([MarshalAs(UnmanagedType.BStr)] out string retVal);
        [PreserveSig] int get_CurrentName([MarshalAs(UnmanagedType.BStr)] out string retVal);
        [PreserveSig] int get_CurrentAcceleratorKey([MarshalAs(UnmanagedType.BStr)] out string retVal);
        [PreserveSig] int get_CurrentAccessKey([MarshalAs(UnmanagedType.BStr)] out string retVal);
        [PreserveSig] int get_CurrentHasKeyboardFocus(out int retVal);
        [PreserveSig] int get_CurrentIsKeyboardFocusable(out int retVal);
        [PreserveSig] int get_CurrentIsEnabled(out int retVal);
        [PreserveSig] int get_CurrentAutomationId([MarshalAs(UnmanagedType.BStr)] out string retVal);
        [PreserveSig] int get_CurrentClassName([MarshalAs(UnmanagedType.BStr)] out string retVal);
        [PreserveSig] int get_CurrentHelpText([MarshalAs(UnmanagedType.BStr)] out string retVal);
        [PreserveSig] int get_CurrentCulture(out int retVal);
        [PreserveSig] int get_CurrentIsControlElement(out int retVal);
        [PreserveSig] int get_CurrentIsContentElement(out int retVal);
        [PreserveSig] int get_CurrentIsPassword(out int retVal);
        [PreserveSig] int get_CurrentNativeWindowHandle(out IntPtr retVal);
        [PreserveSig] int get_CurrentItemType([MarshalAs(UnmanagedType.BStr)] out string retVal);
        [PreserveSig] int get_CurrentIsOffscreen(out int retVal);
        [PreserveSig] int get_CurrentOrientation(out int retVal);
        [PreserveSig] int get_CurrentFrameworkId([MarshalAs(UnmanagedType.BStr)] out string retVal);
        [PreserveSig] int get_CurrentIsRequiredForForm(out int retVal);
        [PreserveSig] int get_CurrentItemStatus([MarshalAs(UnmanagedType.BStr)] out string retVal);
        [PreserveSig] int get_CurrentBoundingRectangle(out RECT retVal);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
}
