using System.Diagnostics;
using System.Drawing;
using System.Windows;
using EverythingonStart.Models;
using EverythingonStart.Services;
using Forms = System.Windows.Forms;

namespace EverythingonStart;

public partial class App : System.Windows.Application
{
    private const string MutexName = "EverythingonStart.SingleInstanceMutex";
    private const string ToggleEventName = "EverythingonStart.ToggleEvent";
    private const string ShutdownEventName = "EverythingonStart.ShutdownEvent";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _toggleWaitHandle;
    private EventWaitHandle? _shutdownWaitHandle;
    private RegisteredWaitHandle? _toggleRegisteredWait;
    private RegisteredWaitHandle? _shutdownRegisteredWait;
    private AppSettingsService? _settingsService;
    private EverythingSearchService? _everythingService;
    private StartMenuIndexService? _startMenuService;
    private WebSuggestionService? _webSuggestionService;
    private IconCacheService? _iconCacheService;
    private ActionExecutorService? _actionExecutor;
    private StartMenuBoundsService? _boundsService;
    private WinKeyHookService? _winKeyHookService;
    private MainWindow? _mainWindow;
    private Forms.NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            RunSelfTestAndExit();
            return;
        }

        bool isReplace = e.Args.Contains("--replace", StringComparer.OrdinalIgnoreCase);
        if (isReplace)
        {
            if (EventWaitHandle.TryOpenExisting(ShutdownEventName, out var shutdownEvent))
            {
                shutdownEvent.Set();
            }

            TerminateOtherInstances();
        }

        bool createdNew = false;
        int attempts = isReplace ? 30 : 1;
        for (int i = 0; i < attempts; i++)
        {
            _instanceMutex = new Mutex(true, MutexName, out createdNew);
            if (createdNew)
                break;

            _instanceMutex.Dispose();
            _instanceMutex = null;
            if (isReplace)
                Thread.Sleep(100);
        }

        if (!createdNew && !isReplace)
        {
            if (EventWaitHandle.TryOpenExisting(ToggleEventName, out var existingEvent))
                existingEvent.Set();

            Shutdown(0);
            return;
        }

        InitializeCoreServices();
        InitializeTrayIcon();
        StartToggleSignalListener();
    }

    private static void TerminateOtherInstances()
    {
        int currentPid = Environment.ProcessId;
        foreach (var proc in Process.GetProcessesByName("EverythingonStart"))
        {
            using (proc)
            {
                if (proc.Id != currentPid)
                {
                    proc.Kill();
                    proc.WaitForExit(2000);
                }
            }
        }
    }

    private void InitializeCoreServices()
    {
        _settingsService = new AppSettingsService();
        _everythingService = new EverythingSearchService(_settingsService.Current.EverythingInstanceName, _settingsService);
        _startMenuService = new StartMenuIndexService();
        _webSuggestionService = new WebSuggestionService(_settingsService);
        _iconCacheService = new IconCacheService();
        _actionExecutor = new ActionExecutorService(_everythingService, _settingsService);
        _boundsService = new StartMenuBoundsService(_settingsService);

        _mainWindow = new MainWindow(
            _settingsService,
            _everythingService,
            _startMenuService,
            _webSuggestionService,
            _iconCacheService,
            _actionExecutor,
            _boundsService
        );
        MainWindow = _mainWindow;
        _mainWindow.PreWarm();

        _winKeyHookService = new WinKeyHookService(_settingsService, _boundsService);
        _winKeyHookService.SetMainWindowHandle(new System.Windows.Interop.WindowInteropHelper(_mainWindow).Handle);
        _mainWindow.AttachWinKeyHookService(_winKeyHookService);
        _winKeyHookService.SingleWinKeyTapped += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_settingsService.Current.TriggerMode == WinKeyTriggerMode.DirectOnSingleWinTap)
                {
                    _mainWindow.TogglePopup();
                }
                else if (_mainWindow.IsVisible)
                {
                    _mainWindow.HidePopup();
                }
            });
        };
        _winKeyHookService.StartMenuTypingIntercepted += typedText =>
        {
            Dispatcher.BeginInvoke(
                () => _mainWindow.ShowPopup(typedText),
                System.Windows.Threading.DispatcherPriority.Send
            );
        };
        _winKeyHookService.AdditionalTypingIntercepted += extraText =>
        {
            Dispatcher.BeginInvoke(
                () => _mainWindow.AppendTypedText(extraText),
                System.Windows.Threading.DispatcherPriority.Send
            );
        };
        _winKeyHookService.Start();
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开搜索面板 (Win)", null, (_, _) => Dispatcher.BeginInvoke(() => _mainWindow?.ShowPopup(null)));
        menu.Items.Add("打开设置中心 (自定义文件类型与筛选器)...", null, (_, _) => Dispatcher.BeginInvoke(() => _mainWindow?.OpenSettingsDialog(0)));
        menu.Items.Add(new Forms.ToolStripSeparator());

        var directModeItem = new Forms.ToolStripMenuItem("单按 Win 键直接呼出搜索面板");
        var typeModeItem = new Forms.ToolStripMenuItem("按 Win 键打开开始菜单后打字接管");
        var initialAllItem = new Forms.ToolStripMenuItem("打开主界面后搜索项：默认“全部”");
        var initialLastItem = new Forms.ToolStripMenuItem("打开主界面后搜索项：上次退出时最后搜索项");

        void SyncModeChecks()
        {
            if (_settingsService == null)
                return;
            directModeItem.Checked = _settingsService.Current.TriggerMode == WinKeyTriggerMode.DirectOnSingleWinTap;
            typeModeItem.Checked = _settingsService.Current.TriggerMode == WinKeyTriggerMode.InterceptTypingInStartMenu;
            initialAllItem.Checked = _settingsService.Current.InitialSearchItem == InitialSearchItemMode.DefaultAll;
            initialLastItem.Checked = _settingsService.Current.InitialSearchItem == InitialSearchItemMode.LastSelectedOnExit;
        }

        directModeItem.Click += (_, _) =>
        {
            if (_settingsService == null)
                return;
            _settingsService.Current.TriggerMode = WinKeyTriggerMode.DirectOnSingleWinTap;
            _settingsService.Save();
            SyncModeChecks();
        };

        typeModeItem.Click += (_, _) =>
        {
            if (_settingsService == null)
                return;
            _settingsService.Current.TriggerMode = WinKeyTriggerMode.InterceptTypingInStartMenu;
            _settingsService.Save();
            SyncModeChecks();
        };

        initialAllItem.Click += (_, _) =>
        {
            if (_settingsService == null)
                return;
            _settingsService.Current.InitialSearchItem = InitialSearchItemMode.DefaultAll;
            _settingsService.Save();
            SyncModeChecks();
        };

        initialLastItem.Click += (_, _) =>
        {
            if (_settingsService == null)
                return;
            _settingsService.Current.InitialSearchItem = InitialSearchItemMode.LastSelectedOnExit;
            if (_mainWindow != null)
                _settingsService.Current.LastSearchCategory = _mainWindow.CurrentCategory;
            _settingsService.Save();
            SyncModeChecks();
        };

        menu.Opening += (_, _) => SyncModeChecks();
        menu.Items.Add(directModeItem);
        menu.Items.Add(typeModeItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(initialAllItem);
        menu.Items.Add(initialLastItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 EverythingonStart", null, (_, _) =>
        {
            _mainWindow?.SaveLastSearchCategory();
            Shutdown();
        });

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "EverythingonStart - 开始菜单极速搜索",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(() => _mainWindow?.TogglePopup());
        };
    }

    private void StartToggleSignalListener()
    {
        _toggleWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, ToggleEventName);
        _toggleRegisteredWait = ThreadPool.RegisterWaitForSingleObject(
            _toggleWaitHandle,
            (_, _) => Dispatcher.BeginInvoke(() => _mainWindow?.ShowPopup(null)),
            null,
            -1,
            executeOnlyOnce: false
        );

        _shutdownWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
        _shutdownRegisteredWait = ThreadPool.RegisterWaitForSingleObject(
            _shutdownWaitHandle,
            (_, _) =>
            {
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }

                _winKeyHookService?.Dispose();
                _everythingService?.Dispose();
                Environment.Exit(0);
            },
            null,
            -1,
            executeOnlyOnce: true
        );
    }

    private void RunSelfTestAndExit()
    {
        using var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var sw = Stopwatch.StartNew();
        var settings = new AppSettingsService();
        using var everything = new EverythingSearchService(settings.Current.EverythingInstanceName, settings);
        var startMenu = new StartMenuIndexService();
        startMenu.RebuildAppIndex();
        var webService = new WebSuggestionService(settings);
        var iconService = new IconCacheService();
        var actionExecutor = new ActionExecutorService(everything, settings);
        var boundsService = new StartMenuBoundsService(settings);

        var window = new MainWindow(settings, everything, startMenu, webService, iconService, actionExecutor, boundsService);
        window.PreWarm();
        window.RevealAndRaiseTaskbar();
        window.PositionLikeStartMenu();
        window.ResultsListBox.UpdateLayout();

        var placement1080p = boundsService.CalculatePlacementForDisplay(1920, 1080, new Rect(0, 0, 1920, 1032), 48);
        var placement1440p = boundsService.CalculatePlacementForDisplay(2560, 1440, new Rect(0, 0, 2560, 1392), 48);

        double firstSelectableRowHeight = 0;
        for (int i = 0; i < window.ResultsListBox.Items.Count; i++)
        {
            if (window.ResultsListBox.Items[i] is SearchListEntry { IsHeader: false } &&
                window.ResultsListBox.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
            {
                container.UpdateLayout();
                firstSelectableRowHeight = container.ActualHeight;
                break;
            }
        }

        // 测试筛选模式（默认 / 跟随 Everything / 自定义）与附加筛选
        var origUsePerCat = settings.Current.UsePerCategoryFilterMode;
        var origDocsMode = settings.Current.DocumentsFilterMode;
        var origDocsCustom = settings.Current.CustomDocumentsFilter;
        var origExtraFilter = settings.Current.ActiveExtraFilter;
        var origSort = settings.Current.SortOrder;

        settings.Current.UsePerCategoryFilterMode = true;
        settings.Current.DocumentsFilterMode = FilterConfigMode.Default;
        string defaultDocQueryText = everything.BuildCategoryQuery("report", SearchCategory.Documents);

        settings.Current.DocumentsFilterMode = FilterConfigMode.FollowEverything;
        string followDocQueryText = everything.BuildCategoryQuery("report", SearchCategory.Documents);

        settings.Current.DocumentsFilterMode = FilterConfigMode.Custom;
        settings.Current.CustomDocumentsFilter = "md;pdf;docx";
        string customDocQueryText = everything.BuildCategoryQuery("report", SearchCategory.Documents);

        settings.Current.ActiveExtraFilter = ExtraTypeFilter.Compressed;
        string extraFilterQueryText = everything.BuildCategoryQuery("archive", SearchCategory.All);
        settings.Current.ActiveExtraFilter = origExtraFilter;

        settings.Current.SortOrder = EverythingSortOrder.FollowEverything;
        var effectiveFollowSort = everything.GetEffectiveSortOrder();

        settings.Current.UsePerCategoryFilterMode = origUsePerCat;
        settings.Current.DocumentsFilterMode = origDocsMode;
        settings.Current.CustomDocumentsFilter = origDocsCustom;
        settings.Current.SortOrder = origSort;

        // 测试打开主界面后初始搜索项：默认“全部” vs 上次退出时最后搜索项
        var origInitialMode = settings.Current.InitialSearchItem;
        var origLastCat = settings.Current.LastSearchCategory;

        window.SelectCategoryTab(SearchCategory.Documents);
        window.SaveLastSearchCategory();

        settings.Current.InitialSearchItem = InitialSearchItemMode.DefaultAll;
        window.ApplyInitialSearchItemOnOpen();
        var defaultAllCategoryOnOpen = window.CurrentCategory;

        settings.Current.InitialSearchItem = InitialSearchItemMode.LastSelectedOnExit;
        window.ApplyInitialSearchItemOnOpen();
        var restoredCategoryOnOpen = window.CurrentCategory;

        settings.Current.InitialSearchItem = origInitialMode;
        settings.Current.LastSearchCategory = origLastCat;
        window.ApplyInitialSearchItemOnOpen();
        settings.Save();

        // 测试左上角返回键在“全部”页关闭搜索窗口、在子分类页返回“全部”页
        window.TabPhotos.IsChecked = true;
        bool closedFromPhotos = window.ExecuteBackOrCloseAction();
        bool returnedToAll = window.TabAll.IsChecked == true;
        bool closedFromAll = window.ExecuteBackOrCloseAction();

        // 测试模拟全屏窗口场景下任务栏 Z-Order 置于全屏窗口上方
        var dummyFullscreen = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Left = 0,
            Top = 0,
            Width = SystemParameters.PrimaryScreenWidth,
            Height = SystemParameters.PrimaryScreenHeight,
            Topmost = true,
            Opacity = 0.01
        };
        dummyFullscreen.Show();
        IntPtr dummyHwnd = new System.Windows.Interop.WindowInteropHelper(dummyFullscreen).Handle;
        window.RevealAndRaiseTaskbar(dummyHwnd);
        bool taskbarAboveFullscreen = EverythingonStart.MainWindow.IsTaskbarAboveWindowInZOrder(dummyHwnd, out string zOrderDiag);
        dummyFullscreen.Close();

        var querySw = Stopwatch.StartNew();
        var docQuery = everything.Query("rar", SearchCategory.Documents, 10);
        var folderQuery = everything.Query("Windows", SearchCategory.Folders, 5);
        querySw.Stop();

        var apps = startMenu.QueryApps("电脑", 5);
        var sysSettings = startMenu.QuerySettings("任务栏", 5);

        using var hook = new WinKeyHookService(settings, boundsService);
        hook.Start();
        hook.Stop();

        GC.Collect(1, GCCollectionMode.Optimized, blocking: true);
        using var selfProc = Process.GetCurrentProcess();
        double workingSetMb = selfProc.WorkingSet64 / (1024.0 * 1024.0);
        double privateMb = selfProc.PrivateMemorySize64 / (1024.0 * 1024.0);
        double managedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

        sw.Stop();
        stdout.WriteLine($"[SelfTest] Engine: {everything.ActiveEngineDescription}");
        stdout.WriteLine($"[SelfTest] EverythingExePath: {actionExecutor.ResolveEverythingExecutablePath() ?? "Not Found"}");
        stdout.WriteLine($"[SelfTest] EverythingConfigSummary: {everything.DetectedConfigSummary}");
        stdout.WriteLine($"[SelfTest] FilterQuery(Default): {defaultDocQueryText}");
        stdout.WriteLine($"[SelfTest] FilterQuery(FollowEverything): {followDocQueryText}");
        stdout.WriteLine($"[SelfTest] FilterQuery(Custom='md;pdf;docx'): {customDocQueryText}");
        stdout.WriteLine($"[SelfTest] ExtraFilter(Compressed): {extraFilterQueryText}");
        stdout.WriteLine($"[SelfTest] EffectiveSort(FollowEverything): {effectiveFollowSort}");
        stdout.WriteLine($"[SelfTest] InitialSearchItem: DefaultAll->{defaultAllCategoryOnOpen}, LastSelectedOnExit->{restoredCategoryOnOpen}");
        stdout.WriteLine($"[SelfTest] BackButton: FromPhotosClosed={closedFromPhotos}, ReturnedToAll={returnedToAll}, FromAllClosed={closedFromAll}");
        stdout.WriteLine($"[SelfTest] FullscreenTaskbarZOrder: TaskbarAboveFullscreen={taskbarAboveFullscreen} ({zOrderDiag})");
        stdout.WriteLine($"[SelfTest] Everything Connected: {everything.IsConnected}");
        stdout.WriteLine($"[SelfTest] DocQuery('rar') Count={docQuery.TotalCount}, Returned={docQuery.Items.Count}");
        stdout.WriteLine($"[SelfTest] FolderQuery('Windows') Count={folderQuery.TotalCount}, Returned={folderQuery.Items.Count}");
        stdout.WriteLine($"[SelfTest] Everything Query Elapsed: {querySw.Elapsed.TotalMilliseconds:F2} ms");
        stdout.WriteLine($"[SelfTest] Apps('电脑') Returned={apps.Count}, Settings('任务栏') Returned={sysSettings.Count}");
        stdout.WriteLine($"[SelfTest] ShowInTaskbar={window.ShowInTaskbar}, NoTaskbarIconConfig={window.HasNoTaskbarIconConfiguration()}");
        stdout.WriteLine($"[SelfTest] WindowBounds=({window.Left:F1}, {window.Top:F1}, {window.Width:F1}x{window.Height:F1}), ScreenHeight={SystemParameters.PrimaryScreenHeight:F1}, Source={boundsService.LastPlacementSource}");
        stdout.WriteLine($"[SelfTest] Adaptive1080p Card=({placement1080p.CardWidthDip:F0}x{placement1080p.CardHeightDip:F0}), Window=({placement1080p.WindowWidthDip:F0}x{placement1080p.WindowHeightDip:F0}), Scale={placement1080p.UiScaleFactor:F2}");
        stdout.WriteLine($"[SelfTest] Adaptive1440p Card=({placement1440p.CardWidthDip:F0}x{placement1440p.CardHeightDip:F0}), Window=({placement1440p.WindowWidthDip:F0}x{placement1440p.WindowHeightDip:F0}), Scale={placement1440p.UiScaleFactor:F2}");
        stdout.WriteLine($"[SelfTest] FirstSelectableRowHeight={firstSelectableRowHeight:F1} px");
        stdout.WriteLine($"[SelfTest] Memory WorkingSet={workingSetMb:F1} MB, Private={privateMb:F1} MB, ManagedHeap={managedMb:F1} MB, Handles={selfProc.HandleCount}, Threads={selfProc.Threads.Count}");
        stdout.WriteLine($"[SelfTest] Total SelfTest Elapsed: {sw.Elapsed.TotalMilliseconds:F2} ms");

        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainWindow?.SaveLastSearchCategory();

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _toggleRegisteredWait?.Unregister(null);
        _shutdownRegisteredWait?.Unregister(null);
        _toggleWaitHandle?.Dispose();
        _shutdownWaitHandle?.Dispose();
        _winKeyHookService?.Dispose();
        _everythingService?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
