using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EverythingonStart.Models;
using EverythingonStart.Services;
using Microsoft.Win32;

namespace EverythingonStart;

public partial class MainWindow : Window
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint ABM_ACTIVATE = 0x00000006;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint GW_HWNDPREV = 3;

    private readonly AppSettingsService _settingsService;
    private readonly EverythingSearchService _everythingService;
    private readonly StartMenuIndexService _startMenuService;
    private readonly WebSuggestionService _webSuggestionService;
    private readonly WebPreviewService _webPreviewService;
    private readonly Func<string, bool> _openWebPreview;
    private readonly ObservableCollection<WebPagePreview> _webPreviewResults = [];
    private CancellationTokenSource? _webPreviewCts;
    private long _webPreviewVersion;
    private string? _webPreviewQuery;
    private WebSearchEngine _webPreviewEngine;
    private readonly IconCacheService _iconCacheService;
    private readonly ActionExecutorService _actionExecutor;
    private readonly StartMenuBoundsService _boundsService;
    private readonly DispatcherTimer _taskbarKeepTopTimer;
    private readonly DispatcherTimer _searchDebounceTimer;
    private readonly DispatcherTimer _inputHandoverFocusTimer;
    private long _inputHandoverFocusReadySinceTick;
    private readonly HashSet<IntPtr> _demotedFullscreenWindows = [];
    private WinKeyHookService? _winKeyHookService;
    private IntPtr _cachedTaskbarHwnd;
    private bool _noTaskbarStyleApplied;
    private readonly AppThemeService _themeService;
    private readonly WindowBackdropService _backdropService;
    private bool _isShowingDefaultLanding;
    private long _searchVersion;
    private int _taskbarKeepTopTicksRemaining;
    private long _openingGraceUntilTick;
    private long _ignoreEscapeUntilTick;

    private readonly ObservableCollection<SearchListEntry> _entries = [];
    private CancellationTokenSource? _searchCts;
    private ITaskbarList2? _taskbarList;
    private SearchCategory _currentCategory = SearchCategory.All;
    private SearchResultItem? _selectedItem;
    private bool _suppressDeactivateHide;
    private bool _suppressLastCategorySave;
    private int _webInsertStartIndex = -1;
    private int _webInsertedCount;

    public SearchCategory CurrentCategory => _currentCategory;

    public MainWindow(
        AppSettingsService settingsService,
        EverythingSearchService everythingService,
        StartMenuIndexService startMenuService,
        WebSuggestionService webSuggestionService,
        IconCacheService iconCacheService,
        ActionExecutorService actionExecutor,
        StartMenuBoundsService boundsService
    )
    {
        _settingsService = settingsService;
        _everythingService = everythingService;
        _startMenuService = startMenuService;
        _webSuggestionService = webSuggestionService;
        _iconCacheService = iconCacheService;
        _actionExecutor = actionExecutor;
        _boundsService = boundsService;
        _webPreviewService = new WebPreviewService();
        _openWebPreview = url => _actionExecutor.ExecuteOpen(new SearchResultItem
        {
            Title = url, Subtitle = "网页预览", FullPath = url, Kind = SearchResultKind.WebSuggestion
        });

        _themeService = AppThemeService.GetOrCreate(_settingsService);
        InitializeComponent();
        _themeService.RegisterWindow(this);
        _backdropService = new WindowBackdropService(this, RootCardBorder, _settingsService, WindowShadowBorder);
        _themeService.ThemeChanged += OnAppThemeChanged;
        Closed += (_, _) => _themeService.ThemeChanged -= OnAppThemeChanged;
        ResultsListBox.ItemsSource = _entries;
        WebPreviewResultsList.ItemsSource = _webPreviewResults;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) CancelWebPreview();
            else if (_selectedItem?.IsWeb == true) UpdatePreviewPane(_selectedItem);
        };
        Closed += (_, _) => { CancelWebPreview(); _webPreviewService.Dispose(); };

        _inputHandoverFocusTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(35)
        };
        _inputHandoverFocusTimer.Tick += (_, _) => RetryInputHandoverFocus();

        _taskbarKeepTopTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _taskbarKeepTopTimer.Tick += (_, _) =>
        {
            if (!IsVisible || _taskbarKeepTopTicksRemaining <= 0 || _demotedFullscreenWindows.Count == 0)
            {
                _taskbarKeepTopTimer.Stop();
                return;
            }

            _taskbarKeepTopTicksRemaining--;
            var selfHwnd = new WindowInteropHelper(this).Handle;
            if (selfHwnd != IntPtr.Zero && GetForegroundWindow() != selfHwnd && !_suppressDeactivateHide)
            {
                ActivateWindowForcefully();
                SearchTextBox.Focus();
            }

            RaiseTaskbarAsyncIfFullscreenActive(selfHwnd);
        };

        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(14)
        };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            TriggerSearchImmediate();
        };

        ApplyInitialSearchItemOnOpen();
        ApplyVisualSettings();
        UpdateEngineBadge();
        UpdateSortAndFilterBadges();
        PopulateDefaultLandingEntries();
    }

    public void AttachWinKeyHookService(WinKeyHookService hookService)
    {
        _winKeyHookService = hookService;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            ApplyNoTaskbarWindowStyle(hwnd);
        }
    }

    public bool HasNoTaskbarIconConfiguration()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return false;

        long exStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
        return !ShowInTaskbar && (exStyle & WS_EX_APPWINDOW) == 0;
    }

    private void ApplyNoTaskbarWindowStyle(IntPtr hwnd)
    {
        if (_noTaskbarStyleApplied || hwnd == IntPtr.Zero)
            return;

        // 不设置 WS_EX_TOOLWINDOW，防止 explorer.exe 的全屏监控器忽略本窗口而将任务栏隐藏在全屏游戏下方；
        // 依靠 WPF ShowInTaskbar="False" 的隐藏 Owner 窗口与 ITaskbarList2::DeleteTab 保证不出现任务栏图标
        long exStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
        long targetExStyle = exStyle & ~WS_EX_APPWINDOW & ~WS_EX_TOOLWINDOW;
        if (targetExStyle != exStyle)
        {
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new IntPtr(targetExStyle));
        }

        EnsureTaskbarListInitialized();
        _taskbarList?.DeleteTab(hwnd);
        _taskbarList?.MarkFullscreenWindow(hwnd, false);
        _noTaskbarStyleApplied = true;
    }

    private void EnsureTaskbarListInitialized()
    {
        if (_taskbarList != null)
            return;

        var taskbarListType = Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"));
        if (taskbarListType != null && Activator.CreateInstance(taskbarListType) is ITaskbarList2 instance)
        {
            instance.HrInit();
            _taskbarList = instance;
        }
    }

    public void ApplyVisualSettings()
    {
        _themeService.ApplyCurrentSettings();
        _backdropService.ApplyCurrentSettings();
    }

    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        _iconCacheService.RefreshThemeIcons();
        foreach (var entry in _entries)
        {
            if (entry.Item is { } item && (item.IsWeb || item.IsSetting))
                _iconCacheService.PopulateIconsAsync(item);
        }
    }

    public void PreWarm()
    {
        Left = -10000;
        Top = -10000;
        ShowActivated = false;
        Show();
        UpdateLayout();
        Hide();
        ShowActivated = true;
    }

    public void TogglePopup()
    {
        if (IsVisible && Opacity > 0.5)
        {
            HidePopup();
        }
        else
        {
            ShowPopup(initialText: null);
        }
    }

    public void ShowPopup(string? initialText)
    {
        long now = Environment.TickCount64;
        bool fromStartMenuTyping = !string.IsNullOrEmpty(initialText);
        _openingGraceUntilTick = now + 650;
        _ignoreEscapeUntilTick = fromStartMenuTyping ? now + 260 : 0;

        ApplyInitialSearchItemOnOpen();
        PositionLikeStartMenu();
        ApplyVisualSettings();
        UpdateEngineBadgeFast();
        UpdateSortAndFilterBadges();

        // 检测呼出前是否存在全屏窗口，仅在存在全屏窗口时执行降级与任务栏置顶
        RevealAndRaiseTaskbar();
        _backdropService.PrepareForShow();

        if (!IsVisible)
        {
            ShowActivated = true;
            Show();
        }

        Opacity = 1.0;
        if (_settingsService.Current.EnablePopupAnimation)
            PlayPopupShowAnimation();
        else
            ResetPopupAnimationTransforms();

        ActivateWindowForcefully();

        if (fromStartMenuTyping)
        {
            SearchTextBox.Text = initialText!;
            SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
            SearchTextBox.Focus();
            Keyboard.Focus(SearchTextBox);

            _inputHandoverFocusReadySinceTick = 0;
            _inputHandoverFocusTimer.Start();
        }
        else
        {
            if (_settingsService.Current.ClearSearchOnOpen)
                SearchTextBox.Clear();
            else
                SearchTextBox.SelectAll();

            SearchTextBox.Focus();
            Keyboard.Focus(SearchTextBox);
        }

        _taskbarKeepTopTimer.Stop();
        if (_demotedFullscreenWindows.Count > 0)
        {
            _taskbarKeepTopTicksRemaining = 4;
            _taskbarKeepTopTimer.Start();
        }

        if (string.IsNullOrWhiteSpace(SearchTextBox.Text) && !_isShowingDefaultLanding)
            PopulateDefaultLandingEntries();
    }

    private void ResetPopupAnimationTransforms()
    {
        RootCardBorder.BeginAnimation(OpacityProperty, null);
        RootCardBorder.Opacity = 1.0;
        PopupSlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        PopupSlideTransform.Y = 0;
        PopupScaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        PopupScaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        PopupScaleTransform.ScaleX = 1.0;
        PopupScaleTransform.ScaleY = 1.0;
    }

    public void AppendTypedText(string extraText)
    {
        if (string.IsNullOrEmpty(extraText))
            return;

        SearchTextBox.Text += extraText;
        SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
        SearchTextBox.Focus();
    }

    private void RetryInputHandoverFocus()
    {
        var hook = _winKeyHookService;
        if (!IsVisible || hook?.IsInputHandoverPending != true)
        {
            _inputHandoverFocusTimer.Stop();
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (GetForegroundWindow() == hwnd && SearchTextBox.IsKeyboardFocused)
        {
            long now = Environment.TickCount64;
            if (_inputHandoverFocusReadySinceTick == 0)
                _inputHandoverFocusReadySinceTick = now;

            // 开始菜单收起时可能再次抢走焦点，连续稳定后才结束按键缓冲。
            if (now - _inputHandoverFocusReadySinceTick >= 120)
            {
                hook.CompleteInputHandover();
                _inputHandoverFocusTimer.Stop();
            }
            return;
        }

        _inputHandoverFocusReadySinceTick = 0;
        ActivateWindowForcefully();
        SearchTextBox.Focus();
        Keyboard.Focus(SearchTextBox);
        SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
    }

    private void PlayPopupShowAnimation()
    {
        var slideEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slideDuration = TimeSpan.FromMilliseconds(130);

        var slideAnim = new DoubleAnimation(18.0, 0.0, slideDuration)
        {
            EasingFunction = slideEase
        };
        Timeline.SetDesiredFrameRate(slideAnim, 60);

        PopupSlideTransform.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            slideAnim
        );
    }

    public void ApplyInitialSearchItemOnOpen()
    {
        var targetCategory = _settingsService.Current.InitialSearchItem == InitialSearchItemMode.LastSelectedOnExit
            ? _settingsService.Current.LastSearchCategory
            : SearchCategory.All;

        _suppressLastCategorySave = true;
        try
        {
            SelectCategoryTab(targetCategory);
        }
        finally
        {
            _suppressLastCategorySave = false;
        }
    }

    public void SaveLastSearchCategory()
    {
        if (_settingsService.Current.LastSearchCategory != _currentCategory)
        {
            _settingsService.Current.LastSearchCategory = _currentCategory;
            _settingsService.Save();
        }
    }

    public void HidePopup()
    {
        CancelWebPreview();
        _inputHandoverFocusTimer.Stop();
        _winKeyHookService?.CompleteInputHandover();
        if (!IsVisible)
            return;

        SaveLastSearchCategory();
        _taskbarKeepTopTimer.Stop();
        _searchDebounceTimer.Stop();
        Interlocked.Increment(ref _searchVersion);
        _searchCts?.Cancel();
        ResetPopupAnimationTransforms();
        Hide();
        RestoreDemotedFullscreenWindows();
    }

    private void RestoreDemotedFullscreenWindows()
    {
        if (_demotedFullscreenWindows.Count == 0)
            return;

        EnsureTaskbarListInitialized();
        uint currentThreadId = GetCurrentThreadId();
        foreach (var fsHwnd in _demotedFullscreenWindows)
        {
            if (fsHwnd != IntPtr.Zero && IsWindow(fsHwnd) && IsWindowVisible(fsHwnd))
            {
                _taskbarList?.MarkFullscreenWindow(fsHwnd, true);
                uint targetThreadId = GetWindowThreadProcessId(fsHwnd, out _);
                // 收起面板时保留用户切换后的层级，不能把后台全屏窗口抬到新前台窗口之上。
                uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER
                             | (targetThreadId != currentThreadId ? SWP_ASYNCWINDOWPOS : 0);
                SetWindowPos(fsHwnd, IntPtr.Zero, 0, 0, 0, 0, flags);
            }
        }

        _demotedFullscreenWindows.Clear();
    }

    public void RevealAndRaiseTaskbar(IntPtr explicitCandidateHwnd = default)
    {
        var selfHwnd = new WindowInteropHelper(this).Handle;
        if (selfHwnd != IntPtr.Zero)
        {
            ApplyNoTaskbarWindowStyle(selfHwnd);
        }

        var taskbarHwnd = GetCachedTaskbarWindowHandle();
        if (_settingsService.Current.KeepTaskbarVisibleInFullscreen)
        {
            DemoteFullscreenWindowsBelowTaskbar(selfHwnd, taskbarHwnd, explicitCandidateHwnd);
        }

        if (_demotedFullscreenWindows.Count > 0)
        {
            RaiseTaskbarAsyncIfFullscreenActive(selfHwnd);
        }
    }

    private void RaiseTaskbarAsyncIfFullscreenActive(IntPtr selfHwnd)
    {
        var taskbarHwnd = GetCachedTaskbarWindowHandle();
        if (taskbarHwnd != IntPtr.Zero)
        {
            ShowWindow(taskbarHwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(
                taskbarHwnd,
                HWND_TOPMOST,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_ASYNCWINDOWPOS
            );
        }

        if (selfHwnd != IntPtr.Zero)
        {
            SetWindowPos(
                selfHwnd,
                HWND_TOPMOST,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW
            );
        }
    }

    private void DemoteFullscreenWindowsBelowTaskbar(
        IntPtr selfHwnd,
        IntPtr taskbarHwnd,
        IntPtr explicitCandidateHwnd = default
    )
    {
        int screenW = GetSystemMetrics(0);
        int screenH = GetSystemMetrics(1);
        if (screenW <= 0 || screenH <= 0)
            return;

        Span<IntPtr> candidates =
        [
            explicitCandidateHwnd,
            _winKeyHookService?.LastAppBeforeWinKeyHwnd ?? IntPtr.Zero,
            GetForegroundWindow()
        ];

        uint currentThreadId = GetCurrentThreadId();
        for (int i = 0; i < candidates.Length; i++)
        {
            var hwnd = candidates[i];
            if (hwnd == IntPtr.Zero || hwnd == selfHwnd || hwnd == taskbarHwnd)
                continue;

            bool duplicate = false;
            for (int j = 0; j < i; j++)
            {
                if (candidates[j] == hwnd)
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate)
                continue;

            if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var rect))
                continue;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (rect.Left > 4 || rect.Top > 4 || width < screenW - 8 || height < screenH - 8)
                continue;

            if (IsShellDesktopOrCoreWindow(hwnd))
                continue;

            EnsureTaskbarListInitialized();
            if (_demotedFullscreenWindows.Add(hwnd))
            {
                _taskbarList?.MarkFullscreenWindow(hwnd, false);
            }

            uint targetThreadId = GetWindowThreadProcessId(hwnd, out _);
            uint asyncFlag = targetThreadId != currentThreadId ? SWP_ASYNCWINDOWPOS : 0;

            SetWindowPos(
                hwnd,
                HWND_NOTOPMOST,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | asyncFlag
            );
            // HWND_NOTOPMOST 已将全屏窗口放到置顶窗口之下。
            // 再插到置顶任务栏之后会重新赋予 WS_EX_TOPMOST，导致 Alt+Tab 选中的普通窗口被遮挡。
        }
    }

    private IntPtr GetCachedTaskbarWindowHandle()
    {
        if (_cachedTaskbarHwnd != IntPtr.Zero && IsWindow(_cachedTaskbarHwnd))
            return _cachedTaskbarHwnd;

        _cachedTaskbarHwnd = FindWindowW("Shell_TrayWnd", null);
        return _cachedTaskbarHwnd;
    }

    private static unsafe bool IsShellDesktopOrCoreWindow(IntPtr hwnd)
    {
        char* clsBuf = stackalloc char[128];
        int len = GetClassNameW(hwnd, clsBuf, 128);
        if (len <= 0)
            return false;

        ReadOnlySpan<char> className = new(clsBuf, len);
        return className.Equals("Progman", StringComparison.OrdinalIgnoreCase)
               || className.Equals("WorkerW", StringComparison.OrdinalIgnoreCase)
               || className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
               || className.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase)
               || className.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase)
               || className.Equals("XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase);
    }

    private static IntPtr FindTaskbarWindowHandle()
    {
        return FindWindowW("Shell_TrayWnd", null);
    }

    public static bool IsTaskbarAboveWindowInZOrder(IntPtr targetHwnd)
        => IsTaskbarAboveWindowInZOrder(targetHwnd, out _);

    public static bool IsTaskbarAboveWindowInZOrder(IntPtr targetHwnd, out string diag)
    {
        if (targetHwnd == IntPtr.Zero)
        {
            diag = "target=0x0";
            return false;
        }

        long targetExStyle = GetWindowLongPtrW(targetHwnd, GWL_EXSTYLE).ToInt64();
        bool targetTopmost = (targetExStyle & WS_EX_TOPMOST) != 0;
        GetWindowRect(targetHwnd, out var rect);

        var taskbarHwnd = FindTaskbarWindowHandle();
        if (taskbarHwnd == IntPtr.Zero)
        {
            diag = $"targetDemoted={!targetTopmost}, rect=({rect.Left},{rect.Top},{rect.Right - rect.Left}x{rect.Bottom - rect.Top})";
            return !targetTopmost;
        }

        long tbExStyle = GetWindowLongPtrW(taskbarHwnd, GWL_EXSTYLE).ToInt64();
        bool tbTopmost = (tbExStyle & WS_EX_TOPMOST) != 0;

        var cur = targetHwnd;
        int stepsToTaskbarAbove = -1;
        for (int step = 1; step < 500 && cur != IntPtr.Zero; step++)
        {
            cur = GetWindow(cur, GW_HWNDPREV);
            if (cur == taskbarHwnd)
            {
                stepsToTaskbarAbove = step;
                break;
            }
        }

        diag = $"tbTop={tbTopmost}, targetDemoted={!targetTopmost}, stepsAbove={stepsToTaskbarAbove}";
        if (!targetTopmost && (tbExStyle == 0 || tbTopmost || stepsToTaskbarAbove > 0))
            return true;

        return stepsToTaskbarAbove > 0;
    }

    public void PositionLikeStartMenu()
    {
        var placement = _boundsService.CalculatePlacement();
        ApplyPlacement(placement);
    }

    public void ApplyPlacement(StartMenuPlacement placement)
    {
        Width = placement.WindowWidthDip;
        Height = placement.WindowHeightDip;
        Left = placement.WindowLeftDip;
        Top = placement.WindowTopDip;

        ContentUiScaleTransform.ScaleX = placement.UiScaleFactor;
        ContentUiScaleTransform.ScaleY = placement.UiScaleFactor;
    }

    private void ActivateWindowForcefully()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var prevFg = GetForegroundWindow();
        if (prevFg != IntPtr.Zero && prevFg != hwnd)
        {
            // 合成 Alt 键扩展输入以解除 Windows 对 StartMenuExperienceHost (AppContainer) 的前台锁
            keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SwitchToThisWindow(hwnd, true);
            keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);

            // 若前一个前台窗口是开始菜单 CoreWindow，向其发送失焦通知促使其立即收起
            PostMessageW(prevFg, WM_ACTIVATE, IntPtr.Zero, hwnd);
        }
        else
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SwitchToThisWindow(hwnd, true);
        }

        Activate();
    }

    private void UpdateEngineBadgeFast()
    {
        if (_everythingService.IsConnected)
        {
            EngineStatusBadge.Text = "Everything 极速引擎";
            EngineStatusBadge.ToolTip = _everythingService.ActiveEngineDescription;
            return;
        }

        _ = Task.Run(() =>
        {
            _everythingService.EnsureConnected();
            Dispatcher.BeginInvoke(() =>
            {
                EngineStatusBadge.Text = _everythingService.IsConnected ? "Everything 极速引擎" : "Everything 未运行";
                EngineStatusBadge.ToolTip = _everythingService.ActiveEngineDescription;
            });
        });
    }

    private void UpdateEngineBadge()
    {
        UpdateEngineBadgeFast();
    }

    public void UpdateSortAndFilterBadges()
    {
        var cur = _settingsService.Current;

        string sortShort = cur.SortOrder switch
        {
            EverythingSortOrder.FollowEverything => "跟随",
            EverythingSortOrder.NameAscending => "名称↑",
            EverythingSortOrder.NameDescending => "名称↓",
            EverythingSortOrder.DateModifiedDescending => "修改↓",
            EverythingSortOrder.DateModifiedAscending => "修改↑",
            EverythingSortOrder.DateCreatedDescending => "创建↓",
            EverythingSortOrder.DateCreatedAscending => "创建↑",
            EverythingSortOrder.SizeDescending => "大小↓",
            EverythingSortOrder.SizeAscending => "大小↑",
            EverythingSortOrder.PathAscending => "路径↑",
            EverythingSortOrder.PathDescending => "路径↓",
            EverythingSortOrder.ExtensionAscending => "扩展名",
            EverythingSortOrder.RunCountDescending => "常用↓",
            _ => "智能"
        };
        SortButtonLabel.Text = $"排序: {sortShort}";

        var effectiveMode = cur.GetEffectiveCategoryMode(_currentCategory);
        string modeShort = effectiveMode switch
        {
            FilterConfigMode.FollowEverything => "跟随",
            FilterConfigMode.Custom => "自定义",
            _ => "默认"
        };

        string extraSuffix = cur.ActiveExtraFilter switch
        {
            ExtraTypeFilter.Audio => "•音频",
            ExtraTypeFilter.Video => "•视频",
            ExtraTypeFilter.Compressed => "•压缩",
            ExtraTypeFilter.Executable => "•程序",
            ExtraTypeFilter.Code => "•代码",
            _ => string.Empty
        };

        FilterButtonLabel.Text = $"筛选: {modeShort}{extraSuffix}";
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        bool hasText = !string.IsNullOrEmpty(SearchTextBox.Text);
        PlaceholderText.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        ClearSearchButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;

        if (!hasText)
        {
            _searchDebounceTimer.Stop();
            TriggerSearchImmediate();
            return;
        }

        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Clear();
        SearchTextBox.Focus();
    }

    private void OnBackToAllCategoryClick(object sender, RoutedEventArgs e)
    {
        ExecuteBackOrCloseAction();
    }

    public bool ExecuteBackOrCloseAction()
    {
        if ((_currentCategory == SearchCategory.All || TabAll.IsChecked == true)
            && _settingsService.Current.BackButtonClosesOnAllTab)
        {
            HidePopup();
            return true;
        }

        TabAll.IsChecked = true;
        SearchTextBox.Focus();
        return false;
    }

    private void OnOpenInEverythingClick(object sender, RoutedEventArgs e)
    {
        if (_actionExecutor.ExecuteOpenInEverything(SearchTextBox.Text))
            HidePopup();
    }

    private void OnCategoryTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: SearchCategory category } && IsLoaded)
        {
            _currentCategory = category;
            if (!_suppressLastCategorySave)
            {
                SaveLastSearchCategory();
            }

            UpdateSortAndFilterBadges();
            TriggerSearchImmediate();
            SearchTextBox.Focus();
        }
    }

    private void TriggerSearch()
    {
        _searchDebounceTimer.Stop();
        TriggerSearchImmediate();
    }

    private void TriggerSearchImmediate()
    {
        long version = Interlocked.Increment(ref _searchVersion);
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var query = SearchTextBox.Text.Trim();
        var category = _currentCategory;

        if (string.IsNullOrEmpty(query))
        {
            PopulateDefaultLandingEntries();
            return;
        }

        _isShowingDefaultLanding = false;
        Task.Run(() => ExecuteLocalSearch(query, category, version, token), token);
    }

    private void PopulateDefaultLandingEntries()
    {
        _isShowingDefaultLanding = true;
        _entries.Clear();
        _webInsertStartIndex = -1;
        _webInsertedCount = 0;

        bool showApps = _settingsService.Current.IncludeStartMenuApps && _currentCategory != SearchCategory.Settings;
        bool showSettings = _settingsService.Current.ShowSettingsInAll || _currentCategory == SearchCategory.Settings;
        if (_currentCategory == SearchCategory.Apps)
        {
            showSettings = false;
        }

        if (showApps)
        {
            int appLimit = _currentCategory == SearchCategory.Apps ? 18 : 8;
            var apps = _startMenuService.QueryApps("", appLimit);
            if (apps.Count > 0)
            {
                _entries.Add(SearchListEntry.CreateHeader("常用应用与系统入口", $"{apps.Count}", SearchCategory.Apps));
                bool first = true;
                foreach (var app in apps)
                {
                    _iconCacheService.PopulateIconsAsync(app);
                    _entries.Add(SearchListEntry.CreateItem(app, isBestMatch: first));
                    first = false;
                }
            }
        }

        if (showSettings)
        {
            int settingLimit = _currentCategory == SearchCategory.Settings ? 18 : 7;
            var settings = _startMenuService.QuerySettings("", settingLimit);
            if (settings.Count > 0)
            {
                _entries.Add(SearchListEntry.CreateHeader("快捷系统设置", $"{settings.Count}+", SearchCategory.Settings));
                bool first = _entries.Count == 0;
                foreach (var setting in settings)
                {
                    _iconCacheService.PopulateIconsAsync(setting);
                    _entries.Add(SearchListEntry.CreateItem(setting, isBestMatch: first));
                    first = false;
                }
            }
        }

        SelectFirstSelectableEntry();
    }

    private void ExecuteLocalSearch(string query, SearchCategory category, long version, CancellationToken token)
    {
        if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
            return;

        var curSettings = _settingsService.Current;
        int maxGroup = Math.Clamp(curSettings.MaxResultsPerGroupInAll, 3, 15);
        int maxSingle = Math.Clamp(curSettings.MaxResultsInSingleCategory, 30, 500);

        if (category == SearchCategory.All)
        {
            var appResults = curSettings.IncludeStartMenuApps
                ? _startMenuService.QueryApps(query, maxGroup)
                : Array.Empty<SearchResultItem>();
            var settingResults = curSettings.ShowSettingsInAll
                ? _startMenuService.QuerySettings(query, Math.Max(3, maxGroup - 2))
                : Array.Empty<SearchResultItem>();

            if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                return;

            var folderResult = _everythingService.Query(query, SearchCategory.Folders, maxGroup, token);
            if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                return;

            var docResult = _everythingService.Query(query, SearchCategory.Documents, maxGroup + 2, token);
            if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                return;

            Dispatcher.BeginInvoke(
                () =>
                {
                    if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                        return;

                    BuildAllCategoryList(query, appResults, settingResults, folderResult, docResult, maxGroup);
                },
                DispatcherPriority.Input
            );

            if (curSettings.ShowWebSuggestionsInAll)
                _ = LoadAndSpliceWebSuggestionsAsync(query, maxCount: 4, token);
        }
        else if (category == SearchCategory.Web)
        {
            var directWeb = _webSuggestionService.CreateDirectWebSearchItem(query);
            _iconCacheService.PopulateIconsAsync(directWeb);

            Dispatcher.BeginInvoke(
                () =>
                {
                    if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                        return;

                    _entries.Clear();
                    _entries.Add(SearchListEntry.CreateHeader("搜索 Web", "实时联想"));
                    _entries.Add(SearchListEntry.CreateItem(directWeb, isBestMatch: true));
                    _webInsertStartIndex = _entries.Count;
                    _webInsertedCount = 0;
                    SelectFirstSelectableEntry();
                },
                DispatcherPriority.Input
            );

            _ = LoadAndSpliceWebSuggestionsAsync(query, maxCount: 12, token);
        }
        else if (category == SearchCategory.Apps)
        {
            var localApps = curSettings.IncludeStartMenuApps
                ? _startMenuService.QueryApps(query, 40)
                : Array.Empty<SearchResultItem>();
            var exeResult = curSettings.IncludeEverythingApps
                ? _everythingService.Query(query, SearchCategory.Apps, maxSingle, token)
                : new EverythingQueryResult(Array.Empty<SearchResultItem>(), 0);
            if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                return;

            Dispatcher.BeginInvoke(
                () =>
                {
                    if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                        return;

                    _entries.Clear();
                    _webInsertStartIndex = -1;
                    _entries.Add(SearchListEntry.CreateHeader("应用", FormatCountBadge(localApps.Count + exeResult.TotalCount)));
                    bool first = true;
                    var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var app in localApps.Concat(exeResult.Items))
                    {
                        if (!seenPaths.Add(app.FullPath))
                            continue;

                        _iconCacheService.PopulateIconsAsync(app);
                        _entries.Add(SearchListEntry.CreateItem(app, isBestMatch: first));
                        first = false;
                    }

                    SelectFirstSelectableEntry();
                },
                DispatcherPriority.Input
            );
        }
        else if (category == SearchCategory.Settings)
        {
            var settings = _startMenuService.QuerySettings(query, 40);
            Dispatcher.BeginInvoke(
                () =>
                {
                    if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                        return;

                    _entries.Clear();
                    _webInsertStartIndex = -1;
                    _entries.Add(SearchListEntry.CreateHeader("设置", $"{settings.Count}"));
                    bool first = true;
                    foreach (var s in settings)
                    {
                        _iconCacheService.PopulateIconsAsync(s);
                        _entries.Add(SearchListEntry.CreateItem(s, isBestMatch: first));
                        first = false;
                    }

                    SelectFirstSelectableEntry();
                },
                DispatcherPriority.Input
            );
        }
        else
        {
            var evResult = _everythingService.Query(query, category, maxSingle, token);
            if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                return;

            string headerName = category switch
            {
                SearchCategory.Documents => "文档与文件",
                SearchCategory.Folders => "文件夹",
                SearchCategory.Photos => "照片与图像",
                _ => "搜索结果"
            };

            Dispatcher.BeginInvoke(
                () =>
                {
                    if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                        return;

                    _entries.Clear();
                    _webInsertStartIndex = -1;
                    _entries.Add(SearchListEntry.CreateHeader(headerName, FormatCountBadge(evResult.TotalCount)));
                    bool first = true;
                    foreach (var item in evResult.Items)
                    {
                        _iconCacheService.PopulateIconsAsync(item);
                        _entries.Add(SearchListEntry.CreateItem(item, isBestMatch: first));
                        first = false;
                    }

                    SelectFirstSelectableEntry();
                },
                DispatcherPriority.Input
            );
        }
    }

    private void BuildAllCategoryList(
        string query,
        IReadOnlyList<SearchResultItem> appResults,
        IReadOnlyList<SearchResultItem> settingResults,
        EverythingQueryResult folderResult,
        EverythingQueryResult docResult,
        int maxGroup
    )
    {
        _entries.Clear();

        var bestMatch = PickBestMatch(query, appResults, settingResults, folderResult.Items, docResult.Items);
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (bestMatch != null)
        {
            usedPaths.Add(bestMatch.FullPath);
            _iconCacheService.PopulateIconsAsync(bestMatch);
            _entries.Add(SearchListEntry.CreateHeader("最佳匹配"));
            _entries.Add(SearchListEntry.CreateItem(bestMatch, isBestMatch: true));

            var secondaryQuick = appResults.FirstOrDefault(a => !usedPaths.Contains(a.FullPath));
            if (secondaryQuick != null)
            {
                usedPaths.Add(secondaryQuick.FullPath);
                _iconCacheService.PopulateIconsAsync(secondaryQuick);
                _entries.Add(SearchListEntry.CreateItem(secondaryQuick));
            }
        }

        if (_settingsService.Current.ShowWebSuggestionsInAll)
        {
            var directWeb = _webSuggestionService.CreateDirectWebSearchItem(query);
            _iconCacheService.PopulateIconsAsync(directWeb);
            _entries.Add(SearchListEntry.CreateHeader("搜索 Web", "10+", SearchCategory.Web));
            _entries.Add(SearchListEntry.CreateItem(directWeb, isBestMatch: bestMatch == null));
            _webInsertStartIndex = _entries.Count;
            _webInsertedCount = 0;
        }
        else
        {
            _webInsertStartIndex = -1;
            _webInsertedCount = 0;
        }

        // 文件夹分组
        var folders = folderResult.Items.Where(f => !usedPaths.Contains(f.FullPath)).Take(Math.Max(2, maxGroup - 2)).ToList();
        if (folders.Count > 0)
        {
            _entries.Add(SearchListEntry.CreateHeader("文件夹", FormatCountBadge(folderResult.TotalCount), SearchCategory.Folders));
            foreach (var folder in folders)
            {
                usedPaths.Add(folder.FullPath);
                _iconCacheService.PopulateIconsAsync(folder);
                _entries.Add(SearchListEntry.CreateItem(folder));
            }
        }

        // 文档与文件分组
        var docs = docResult.Items.Where(d => !usedPaths.Contains(d.FullPath)).Take(maxGroup).ToList();
        if (docs.Count > 0)
        {
            _entries.Add(SearchListEntry.CreateHeader("文档", FormatCountBadge(docResult.TotalCount), SearchCategory.Documents));
            foreach (var doc in docs)
            {
                usedPaths.Add(doc.FullPath);
                _iconCacheService.PopulateIconsAsync(doc);
                _entries.Add(SearchListEntry.CreateItem(doc));
            }
        }

        // 其余应用与设置分组
        var remainingApps = appResults.Where(a => !usedPaths.Contains(a.FullPath)).Take(Math.Max(2, maxGroup - 2)).ToList();
        if (remainingApps.Count > 0)
        {
            _entries.Add(SearchListEntry.CreateHeader("应用", $"{appResults.Count}", SearchCategory.Apps));
            foreach (var app in remainingApps)
            {
                _iconCacheService.PopulateIconsAsync(app);
                _entries.Add(SearchListEntry.CreateItem(app));
            }
        }

        if (_settingsService.Current.ShowSettingsInAll)
        {
            var remainingSettings = settingResults.Where(s => !usedPaths.Contains(s.FullPath)).Take(3).ToList();
            if (remainingSettings.Count > 0)
            {
                _entries.Add(SearchListEntry.CreateHeader("设置", $"{settingResults.Count}", SearchCategory.Settings));
                foreach (var setting in remainingSettings)
                {
                    _iconCacheService.PopulateIconsAsync(setting);
                    _entries.Add(SearchListEntry.CreateItem(setting));
                }
            }
        }

        SelectFirstSelectableEntry();
    }

    private static SearchResultItem? PickBestMatch(
        string query,
        IReadOnlyList<SearchResultItem> apps,
        IReadOnlyList<SearchResultItem> settings,
        IReadOnlyList<SearchResultItem> folders,
        IReadOnlyList<SearchResultItem> docs
    )
    {
        var qLower = query.ToLowerInvariant();
        bool looksLikeFileName = query.Contains('.') && query.Length > 3;

        if (looksLikeFileName)
        {
            var exactDoc = docs.FirstOrDefault(d => d.Title.Equals(query, StringComparison.OrdinalIgnoreCase))
                           ?? docs.FirstOrDefault(d => d.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                           ?? docs.FirstOrDefault();
            if (exactDoc != null)
                return exactDoc;

            var exactFolder = folders.FirstOrDefault();
            if (exactFolder != null)
                return exactFolder;
        }

        var exactApp = apps.FirstOrDefault(a => a.Title.Equals(query, StringComparison.OrdinalIgnoreCase) || a.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase));
        if (exactApp != null)
            return exactApp;

        var romanizedApp = apps.FirstOrDefault(a => PinyinSearchService.GetMatchScore(a.Title, query) is > 0 and < 70);
        if (romanizedApp != null)
            return romanizedApp;

        var topDoc = docs.FirstOrDefault(d => d.Title.StartsWith(qLower, StringComparison.OrdinalIgnoreCase));
        if (topDoc != null)
            return topDoc;

        var topFolder = folders.FirstOrDefault(f => f.Title.StartsWith(qLower, StringComparison.OrdinalIgnoreCase));
        if (topFolder != null)
            return topFolder;

        return docs.FirstOrDefault()
               ?? folders.FirstOrDefault()
               ?? apps.FirstOrDefault()
               ?? settings.FirstOrDefault();
    }

    private async Task LoadAndSpliceWebSuggestionsAsync(string query, int maxCount, CancellationToken token)
    {
        await Task.Delay(110, token);
        if (token.IsCancellationRequested)
            return;

        var suggestions = await _webSuggestionService.GetWebSuggestionsAsync(query, maxCount, token);
        if (token.IsCancellationRequested || suggestions.Count <= 1)
            return;

        await Dispatcher.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested || _webInsertStartIndex < 0 || _webInsertStartIndex > _entries.Count)
                return;

            var currentSelected = ResultsListBox.SelectedItem;

            for (int i = 0; i < _webInsertedCount; i++)
            {
                if (_webInsertStartIndex < _entries.Count)
                    _entries.RemoveAt(_webInsertStartIndex);
            }

            _webInsertedCount = 0;
            foreach (var suggestion in suggestions.Skip(1))
            {
                _iconCacheService.PopulateIconsAsync(suggestion);
                _entries.Insert(_webInsertStartIndex + _webInsertedCount, SearchListEntry.CreateItem(suggestion));
                _webInsertedCount++;
            }

            if (currentSelected != null)
                ResultsListBox.SelectedItem = currentSelected;
        });
    }

    private static string FormatCountBadge(int count) =>
        count switch
        {
            <= 0 => "",
            > 10 => "10+",
            > 6 => "6+",
            _ => count.ToString()
        };

    private void SelectFirstSelectableEntry()
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (!_entries[i].IsHeader)
            {
                ResultsListBox.SelectedIndex = i;
                ResultsListBox.ScrollIntoView(_entries[i]);
                return;
            }
        }

        UpdatePreviewPane(null);
    }

    private void OnResultsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsListBox.SelectedItem is SearchListEntry { IsHeader: false, Item: { } item })
        {
            if (_selectedItem != null)
                _selectedItem.PropertyChanged -= OnSelectedItemPropertyChanged;

            _selectedItem = item;
            _selectedItem.PropertyChanged += OnSelectedItemPropertyChanged;
            UpdatePreviewPane(item);
        }
        else
        {
            if (_selectedItem != null)
                _selectedItem.PropertyChanged -= OnSelectedItemPropertyChanged;
            _selectedItem = null;
            UpdatePreviewPane(null);
        }
    }

    private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is SearchResultItem item && ReferenceEquals(item, _selectedItem))
        {
            PreviewIconImage.Source = item.DisplayPreviewIcon;
        }
    }

    private void UpdatePreviewPane(SearchResultItem? item)
    {
        if (item?.IsWeb == true)
        {
            FileDetailsPreviewPanel.Visibility = Visibility.Collapsed;
            WebResultsPreviewPanel.Visibility = Visibility.Visible;
            StartWebPreview(item);
            return;
        }
        CancelWebPreview();
        WebResultsPreviewPanel.Visibility = Visibility.Collapsed;
        FileDetailsPreviewPanel.Visibility = Visibility.Visible;
        if (item == null)
        {
            PreviewIconImage.Source = null;
            PreviewTitleText.Text = "未找到匹配项";
            PreviewSubtitleText.Text = "请尝试输入其他关键词或直接回车进行 Web 搜索";
            PreviewLocationText.Text = "-";
            PreviewModifiedText.Text = "-";
            PreviewStatusText.Text = "-";
            BtnActionOpen.IsEnabled = false;
            BtnActionAdmin.Visibility = Visibility.Collapsed;
            BtnActionOpenLocation.Visibility = Visibility.Collapsed;
            BtnActionCopyPath.Visibility = Visibility.Collapsed;
            return;
        }

        _iconCacheService.PopulateLargeIconAsync(item);
        PreviewIconImage.Source = item.DisplayPreviewIcon;
        PreviewTitleText.Text = item.Title;
        PreviewSubtitleText.Text = item.Subtitle;
        PreviewLocationText.Text = item.LocationDisplay;
        PreviewModifiedText.Text = item.FormattedModifiedTime;
        PreviewStatusText.Text = item.FormattedSizeOrStatus;

        BtnActionOpen.IsEnabled = true;
        BtnActionAdmin.Visibility = item.SupportsRunAsAdmin ? Visibility.Visible : Visibility.Collapsed;
        BtnActionOpenLocation.Visibility = item.SupportsOpenLocation ? Visibility.Visible : Visibility.Collapsed;
        BtnActionCopyPath.Visibility = item.SupportsCopyPath ? Visibility.Visible : Visibility.Collapsed;

        var ext = Path.GetExtension(item.FullPath);
        bool isExe = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                     || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                     || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase)
                     || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                     || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        AdminActionLabel.Text = isExe ? "以管理员身份运行" : "管理员取得所有权";
        CopyPathActionLabel.Text = "复制路径";
    }

    private void CancelWebPreview()
    {
        Interlocked.Increment(ref _webPreviewVersion);
        _webPreviewCts?.Cancel();
        _webPreviewCts?.Dispose();
        _webPreviewCts = null;
        _webPreviewQuery = null;
    }

    private void StartWebPreview(SearchResultItem item)
    {
        string query = WebPreviewService.GetQuery(item);
        var engine = _settingsService.Current.SearchEngine;
        if (_webPreviewQuery == query && _webPreviewEngine == engine && _webPreviewCts != null)
            return;
        CancelWebPreview();
        _webPreviewQuery = query;
        _webPreviewEngine = engine;
        _webPreviewResults.Clear();
        WebPreviewScrollViewer.ScrollToTop();
        WebPreviewQueryText.Text = query.Length == 0 ? "网页预览" : $"“{query}”的网页预览";
        WebPreviewEngineText.Text = engine == WebSearchEngine.Baidu ? "百度" : engine.ToString();
        WebPreviewMessageText.Text = query.Length == 0 ? "请选择搜索词，在浏览器继续查看。" : "正在加载网页结果…";
        WebPreviewMessageText.Visibility = Visibility.Visible;
        if (!IsVisible || query.Length == 0)
            return;
        _webPreviewCts = new CancellationTokenSource();
        long version = Volatile.Read(ref _webPreviewVersion);
        _ = LoadWebPreviewAsync(query, engine, version, _webPreviewCts.Token);
    }

    private async Task LoadWebPreviewAsync(string query, WebSearchEngine engine, long version, CancellationToken token)
    {
        try
        {
            // Arrow-key navigation and typing must not send one HTTP request for every transient selection.
            await Task.Delay(250, token);
            var response = await _webPreviewService.GetResultsAsync(query, engine, token);
            token.ThrowIfCancellationRequested();
            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || version != Volatile.Read(ref _webPreviewVersion)
                    || !IsVisible || _selectedItem?.IsWeb != true || WebPreviewService.GetQuery(_selectedItem) != query)
                    return;
                _webPreviewResults.Clear();
                foreach (var result in response.Results) _webPreviewResults.Add(result);
                WebPreviewMessageText.Text = response.Message;
                WebPreviewMessageText.Visibility = response.Status == WebPreviewStatus.Ready ? Visibility.Collapsed : Visibility.Visible;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!token.IsCancellationRequested && version == Volatile.Read(ref _webPreviewVersion))
                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || version != Volatile.Read(ref _webPreviewVersion)
                        || !IsVisible || _selectedItem?.IsWeb != true) return;
                    WebPreviewMessageText.Text = "网页预览暂不可用，请在浏览器继续。";
                    WebPreviewMessageText.Visibility = Visibility.Visible;
                });
        }
    }

    private void OnWebPreviewLinkClick(object sender, RoutedEventArgs e)
    {
        string? url = (sender as FrameworkElement)?.Tag switch
        {
            WebPagePreview result => result.Url,
            WebPreviewLink link => link.Url,
            _ => null
        };
        if (url != null && WebPreviewService.IsSafeWebUrl(url) && _openWebPreview(url)) HidePopup();
    }

    private void OnWebPreviewBrowserClick(object sender, RoutedEventArgs e)
    {
        if (_selectedItem?.IsWeb == true && _openWebPreview(WebPreviewService.GetSearchUrl(WebPreviewService.GetQuery(_selectedItem), _settingsService.Current.SearchEngine)))
            HidePopup();
    }

    private void OnWebPreviewRetryClick(object sender, RoutedEventArgs e)
    {
        _webPreviewService.ClearCache();
        CancelWebPreview();
        if (_selectedItem?.IsWeb == true) StartWebPreview(_selectedItem);
    }

    private void OnSectionHeaderMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchListEntry { IsHeader: true, TargetCategory: { } targetCat } })
        {
            SelectCategoryTab(targetCat);
            e.Handled = true;
        }
    }

    private void OnResultRowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchListEntry { IsHeader: false } entry })
        {
            ResultsListBox.SelectedItem = entry;
        }
    }

    private void OnResultsMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsListBox.SelectedItem is SearchListEntry { IsHeader: false, Item: { } item })
        {
            if (_actionExecutor.ExecuteOpen(item))
                HidePopup();
        }
    }

    private void OnActionOpenClick(object sender, RoutedEventArgs e)
    {
        if (_actionExecutor.ExecuteOpen(_selectedItem))
            HidePopup();
    }

    private void OnActionAdminClick(object sender, RoutedEventArgs e)
    {
        if (_actionExecutor.ExecuteRunAsAdminOrTakeOwnership(_selectedItem))
            HidePopup();
    }

    private void OnActionOpenLocationClick(object sender, RoutedEventArgs e)
    {
        if (_actionExecutor.ExecuteOpenFileLocation(_selectedItem))
            HidePopup();
    }

    private void OnActionCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (_actionExecutor.ExecuteCopyPath(_selectedItem))
            CopyPathActionLabel.Text = "已复制完整路径 ✓";
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (Environment.TickCount64 < _ignoreEscapeUntilTick)
            {
                e.Handled = true;
                return;
            }

            if (_settingsService.Current.EscClearsSearchFirst && !string.IsNullOrEmpty(SearchTextBox.Text))
                SearchTextBox.Clear();
            else
                HidePopup();

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            MoveSelection(1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            MoveSelection(-1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab)
        {
            bool reverse = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            CycleCategoryTab(reverse ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            var modifiers = Keyboard.Modifiers;
            if ((modifiers & ModifierKeys.Control) != 0 && (modifiers & ModifierKeys.Shift) != 0)
            {
                if (_actionExecutor.ExecuteRunAsAdminOrTakeOwnership(_selectedItem))
                    HidePopup();
            }
            else if ((modifiers & ModifierKeys.Shift) != 0)
            {
                if (_actionExecutor.ExecuteOpenInEverything(SearchTextBox.Text))
                    HidePopup();
            }
            else if ((modifiers & ModifierKeys.Control) != 0)
            {
                if (_actionExecutor.ExecuteOpenFileLocation(_selectedItem))
                    HidePopup();
            }
            else
            {
                if (_actionExecutor.ExecuteOpen(_selectedItem))
                    HidePopup();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (_actionExecutor.ExecuteCopyPath(_selectedItem))
                CopyPathActionLabel.Text = "已复制完整路径 ✓";

            e.Handled = true;
        }
    }

    private void MoveSelection(int direction)
    {
        if (_entries.Count == 0)
            return;

        int current = ResultsListBox.SelectedIndex;
        int index = current;

        for (int step = 0; step < _entries.Count; step++)
        {
            index += direction;
            if (index < 0)
                index = _entries.Count - 1;
            else if (index >= _entries.Count)
                index = 0;

            if (!_entries[index].IsHeader)
            {
                ResultsListBox.SelectedIndex = index;
                ResultsListBox.ScrollIntoView(_entries[index]);
                return;
            }
        }
    }

    private void CycleCategoryTab(int delta)
    {
        RadioButton[] tabs = [TabAll, TabApps, TabDocs, TabWeb, TabSettings, TabFolders, TabPhotos];
        int currentIndex = Array.FindIndex(tabs, t => t.IsChecked == true);
        if (currentIndex < 0)
            currentIndex = 0;

        int nextIndex = (currentIndex + delta + tabs.Length) % tabs.Length;
        tabs[nextIndex].IsChecked = true;
    }

    public void SelectCategoryTab(SearchCategory category)
    {
        _currentCategory = category;
        RadioButton target = category switch
        {
            SearchCategory.Apps => TabApps,
            SearchCategory.Documents => TabDocs,
            SearchCategory.Web => TabWeb,
            SearchCategory.Settings => TabSettings,
            SearchCategory.Folders => TabFolders,
            SearchCategory.Photos => TabPhotos,
            _ => TabAll
        };
        if (target.IsChecked != true)
        {
            target.IsChecked = true;
        }
        else if (IsLoaded)
        {
            UpdateSortAndFilterBadges();
            TriggerSearchImmediate();
        }
    }

    private void OnSortSelectorButtonClick(object sender, RoutedEventArgs e)
    {
        _suppressDeactivateHide = true;

        var menu = new ContextMenu();
        var s = _settingsService.Current;

        void AddSortItem(string header, EverythingSortOrder sortOrder)
        {
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = s.SortOrder == sortOrder
            };
            item.Click += (_, _) =>
            {
                s.SortOrder = sortOrder;
                _settingsService.Save();
                UpdateSortAndFilterBadges();
                TriggerSearch();
            };
            menu.Items.Add(item);
        }

        AddSortItem("跟随 Everything 设置", EverythingSortOrder.FollowEverything);
        menu.Items.Add(new Separator());
        AddSortItem("运行次数（高频优先 · 默认）", EverythingSortOrder.RunCountDescending);
        AddSortItem("修改时间（从新到旧）", EverythingSortOrder.DateModifiedDescending);
        AddSortItem("修改时间（从旧到新）", EverythingSortOrder.DateModifiedAscending);
        menu.Items.Add(new Separator());
        AddSortItem("名称（A → Z 升序）", EverythingSortOrder.NameAscending);
        AddSortItem("名称（Z → A 降序）", EverythingSortOrder.NameDescending);
        AddSortItem("路径（A → Z 升序）", EverythingSortOrder.PathAscending);
        AddSortItem("路径（Z → A 降序）", EverythingSortOrder.PathDescending);
        menu.Items.Add(new Separator());
        AddSortItem("大小（从大到小）", EverythingSortOrder.SizeDescending);
        AddSortItem("大小（从小到大）", EverythingSortOrder.SizeAscending);
        AddSortItem("扩展名（A → Z 升序）", EverythingSortOrder.ExtensionAscending);
        AddSortItem("创建时间（从新到旧）", EverythingSortOrder.DateCreatedDescending);

        menu.Closed += (_, _) =>
        {
            _suppressDeactivateHide = false;
            SearchTextBox.Focus();
        };

        menu.PlacementTarget = SortSelectorButton;
        menu.IsOpen = true;
    }

    private void OnFilterSelectorButtonClick(object sender, RoutedEventArgs e)
    {
        _suppressDeactivateHide = true;

        var menu = new ContextMenu();
        var s = _settingsService.Current;

        void AddExtraFilterItem(string header, ExtraTypeFilter filter)
        {
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = s.ActiveExtraFilter == filter
            };
            item.Click += (_, _) =>
            {
                s.ActiveExtraFilter = filter;
                _settingsService.Save();
                UpdateSortAndFilterBadges();
                TriggerSearch();
            };
            menu.Items.Add(item);
        }

        AddExtraFilterItem("附加类型筛选：不限（按当前分类标签）", ExtraTypeFilter.None);
        AddExtraFilterItem("附加类型筛选：仅音频文件 (mp3/flac/wav...)", ExtraTypeFilter.Audio);
        AddExtraFilterItem("附加类型筛选：仅视频文件 (mp4/mkv/mov...)", ExtraTypeFilter.Video);
        AddExtraFilterItem("附加类型筛选：仅压缩包 (zip/7z/rar...)", ExtraTypeFilter.Compressed);
        AddExtraFilterItem("附加类型筛选：仅可执行程序与安装包 (exe/msi/bat...)", ExtraTypeFilter.Executable);
        AddExtraFilterItem("附加类型筛选：仅代码与脚本文件 (cs/py/ts/json...)", ExtraTypeFilter.Code);

        menu.Items.Add(new Separator());

        var followToggle = new MenuItem
        {
            Header = "搜索匹配修饰项：跟随 Everything 设置",
            IsCheckable = true,
            IsChecked = s.FollowEverythingSearchFlags
        };
        followToggle.Click += (_, _) =>
        {
            s.FollowEverythingSearchFlags = followToggle.IsChecked;
            _settingsService.Save();
            UpdateSortAndFilterBadges();
            TriggerSearch();
        };
        menu.Items.Add(followToggle);

        var matchCaseItem = new MenuItem
        {
            Header = "区分大小写 (Match Case)",
            IsCheckable = true,
            IsChecked = s.MatchCase
        };
        matchCaseItem.Click += (_, _) =>
        {
            s.FollowEverythingSearchFlags = false;
            s.MatchCase = matchCaseItem.IsChecked;
            _settingsService.Save();
            UpdateSortAndFilterBadges();
            TriggerSearch();
        };
        menu.Items.Add(matchCaseItem);

        var matchWholeWordItem = new MenuItem
        {
            Header = "全字匹配 (Whole Word)",
            IsCheckable = true,
            IsChecked = s.MatchWholeWord
        };
        matchWholeWordItem.Click += (_, _) =>
        {
            s.FollowEverythingSearchFlags = false;
            s.MatchWholeWord = matchWholeWordItem.IsChecked;
            _settingsService.Save();
            UpdateSortAndFilterBadges();
            TriggerSearch();
        };
        menu.Items.Add(matchWholeWordItem);

        var matchPathItem = new MenuItem
        {
            Header = "匹配完整路径 (Match Path)",
            IsCheckable = true,
            IsChecked = s.MatchPath
        };
        matchPathItem.Click += (_, _) =>
        {
            s.FollowEverythingSearchFlags = false;
            s.MatchPath = matchPathItem.IsChecked;
            _settingsService.Save();
            UpdateSortAndFilterBadges();
            TriggerSearch();
        };
        menu.Items.Add(matchPathItem);

        var enableRegexItem = new MenuItem
        {
            Header = "启用正则表达式 (Regex)",
            IsCheckable = true,
            IsChecked = s.EnableRegex
        };
        enableRegexItem.Click += (_, _) =>
        {
            s.FollowEverythingSearchFlags = false;
            s.EnableRegex = enableRegexItem.IsChecked;
            _settingsService.Save();
            UpdateSortAndFilterBadges();
            TriggerSearch();
        };
        menu.Items.Add(enableRegexItem);

        menu.Items.Add(new Separator());

        var customizeFiltersItem = new MenuItem
        {
            Header = "自定义各子选项文件类型与 Everything 筛选器...",
            Icon = CreateMingCuteSettingsMenuIcon()
        };
        customizeFiltersItem.Click += (_, _) => OpenSettingsDialog(0);
        menu.Items.Add(customizeFiltersItem);

        menu.Closed += (_, _) =>
        {
            _suppressDeactivateHide = false;
            SearchTextBox.Focus();
        };

        menu.PlacementTarget = FilterSelectorButton;
        menu.IsOpen = true;
    }

    private static System.Windows.Shapes.Path CreateMingCuteSettingsMenuIcon()
    {
        var icon = new System.Windows.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Data = System.Windows.Media.Geometry.Parse(IconCacheService.MingCuteSettings4LinePathData)
        };
        icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, ThemeResourceKeys.AccentTextBrush);
        return icon;
    }

    private void OnSettingsWindowButtonClick(object sender, RoutedEventArgs e)
    {
        OpenSettingsDialog(0);
    }

    public void OpenSettingsDialog(int initialTabIndex = 0)
    {
        _suppressDeactivateHide = true;
        try
        {
            var dialog = new SettingsWindow(
                _settingsService,
                _everythingService,
                _boundsService,
                () =>
                {
                    ApplyVisualSettings();
                    PositionLikeStartMenu();
                    UpdateEngineBadge();
                    UpdateSortAndFilterBadges();
                    TriggerSearch();
                },
                initialTabIndex
            );
            if (IsVisible)
            {
                dialog.Owner = this;
            }

            dialog.ShowDialog();
        }
        finally
        {
            _suppressDeactivateHide = false;
            if (IsVisible)
            {
                ActivateWindowForcefully();
                SearchTextBox.Focus();
            }
        }
    }

    private void OnOptionsMenuButtonClick(object sender, RoutedEventArgs e)
    {
        _suppressDeactivateHide = true;

        var menu = new ContextMenu();

        var openSettingsItem = new MenuItem
        {
            Header = "打开设置中心 (自定义文件类型与筛选器)...",
            Icon = CreateMingCuteSettingsMenuIcon()
        };
        openSettingsItem.Click += (_, _) => OpenSettingsDialog(0);

        var modeDirectItem = new MenuItem
        {
            Header = "Win 键模式：单按 Win 键直接呼出搜索面板（推荐）",
            IsCheckable = true,
            IsChecked = _settingsService.Current.TriggerMode == WinKeyTriggerMode.DirectOnSingleWinTap
        };
        modeDirectItem.Click += (_, _) =>
        {
            _settingsService.Current.TriggerMode = WinKeyTriggerMode.DirectOnSingleWinTap;
            _settingsService.Save();
        };

        var modeTypeItem = new MenuItem
        {
            Header = "Win 键模式：按 Win 打开开始菜单后打字瞬间接管",
            IsCheckable = true,
            IsChecked = _settingsService.Current.TriggerMode == WinKeyTriggerMode.InterceptTypingInStartMenu
        };
        modeTypeItem.Click += (_, _) =>
        {
            _settingsService.Current.TriggerMode = WinKeyTriggerMode.InterceptTypingInStartMenu;
            _settingsService.Save();
        };

        var engineBing = new MenuItem
        {
            Header = "Web 搜索引擎：Bing (必应)",
            IsCheckable = true,
            IsChecked = _settingsService.Current.SearchEngine == WebSearchEngine.Bing
        };
        engineBing.Click += (_, _) => SetSearchEngine(WebSearchEngine.Bing);

        var engineGoogle = new MenuItem
        {
            Header = "Web 搜索引擎：Google",
            IsCheckable = true,
            IsChecked = _settingsService.Current.SearchEngine == WebSearchEngine.Google
        };
        engineGoogle.Click += (_, _) => SetSearchEngine(WebSearchEngine.Google);

        var engineBaidu = new MenuItem
        {
            Header = "Web 搜索引擎：百度",
            IsCheckable = true,
            IsChecked = _settingsService.Current.SearchEngine == WebSearchEngine.Baidu
        };
        engineBaidu.Click += (_, _) => SetSearchEngine(WebSearchEngine.Baidu);

        var engineDuckDuckGo = new MenuItem
        {
            Header = "Web 搜索引擎：DuckDuckGo",
            IsCheckable = true,
            IsChecked = _settingsService.Current.SearchEngine == WebSearchEngine.DuckDuckGo
        };
        engineDuckDuckGo.Click += (_, _) => SetSearchEngine(WebSearchEngine.DuckDuckGo);

        var initialDefaultAllItem = new MenuItem
        {
            Header = "打开主界面后搜索项：默认“全部”",
            IsCheckable = true,
            IsChecked = _settingsService.Current.InitialSearchItem == InitialSearchItemMode.DefaultAll
        };
        initialDefaultAllItem.Click += (_, _) =>
        {
            _settingsService.Current.InitialSearchItem = InitialSearchItemMode.DefaultAll;
            _settingsService.Save();
        };

        var initialLastExitItem = new MenuItem
        {
            Header = "打开主界面后搜索项：上次退出时最后搜索项",
            IsCheckable = true,
            IsChecked = _settingsService.Current.InitialSearchItem == InitialSearchItemMode.LastSelectedOnExit
        };
        initialLastExitItem.Click += (_, _) =>
        {
            _settingsService.Current.InitialSearchItem = InitialSearchItemMode.LastSelectedOnExit;
            _settingsService.Current.LastSearchCategory = _currentCategory;
            _settingsService.Save();
        };

        var keepTaskbarItem = new MenuItem
        {
            Header = "全屏游戏/应用呼出搜索时保留底部任务栏",
            IsCheckable = true,
            IsChecked = _settingsService.Current.KeepTaskbarVisibleInFullscreen
        };
        keepTaskbarItem.Click += (_, _) =>
        {
            _settingsService.Current.KeepTaskbarVisibleInFullscreen = keepTaskbarItem.IsChecked;
            _settingsService.Save();
        };

        var autostartItem = new MenuItem
        {
            Header = "开机自动启动 EverythingonStart",
            IsCheckable = true,
            IsChecked = _settingsService.IsAutostartEnabled()
        };
        autostartItem.Click += (_, _) => _settingsService.SetAutostart(autostartItem.IsChecked);

        var exitItem = new MenuItem
        {
            Header = "退出 EverythingonStart"
        };
        exitItem.Click += (_, _) =>
        {
            SaveLastSearchCategory();
            Application.Current.Shutdown();
        };

        menu.Items.Add(openSettingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(initialDefaultAllItem);
        menu.Items.Add(initialLastExitItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(modeDirectItem);
        menu.Items.Add(modeTypeItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(engineBing);
        menu.Items.Add(engineGoogle);
        menu.Items.Add(engineBaidu);
        menu.Items.Add(engineDuckDuckGo);
        menu.Items.Add(new Separator());
        menu.Items.Add(keepTaskbarItem);
        menu.Items.Add(autostartItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        menu.Closed += (_, _) =>
        {
            _suppressDeactivateHide = false;
            SearchTextBox.Focus();
        };

        menu.PlacementTarget = OptionsMenuButton;
        menu.IsOpen = true;
    }

    private void SetSearchEngine(WebSearchEngine engine)
    {
        _settingsService.Current.SearchEngine = engine;
        _settingsService.Save();
        TriggerSearch();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_suppressDeactivateHide)
            return;

        if (!_settingsService.Current.AutoHideOnDeactivate)
            return;

        if (Environment.TickCount64 < _openingGraceUntilTick)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible)
                {
                    ActivateWindowForcefully();
                    SearchTextBox.Focus();
                }
            });
            return;
        }

        HidePopup();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int GetClassNameW(IntPtr hWnd, char* lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [ComImport]
    [Guid("602D4995-B13A-429B-A66E-1935E44F4317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList2
    {
        [PreserveSig]
        int HrInit();

        [PreserveSig]
        int AddTab(IntPtr hwnd);

        [PreserveSig]
        int DeleteTab(IntPtr hwnd);

        [PreserveSig]
        int ActivateTab(IntPtr hwnd);

        [PreserveSig]
        int SetActiveAlt(IntPtr hwnd);

        [PreserveSig]
        int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags
    );

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern bool K32EmptyWorkingSet(IntPtr hProcess);
}
