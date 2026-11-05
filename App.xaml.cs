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
    private AppSettingsService? _settingsService;
    private EverythingSearchService? _everythingService;
    private StartMenuIndexService? _startMenuService;
    private WebSuggestionService? _webSuggestionService;
    private IconCacheService? _iconCacheService;
    private ActionExecutorService? _actionExecutor;
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
        _everythingService = new EverythingSearchService(_settingsService.Current.EverythingInstanceName);
        _startMenuService = new StartMenuIndexService();
        _webSuggestionService = new WebSuggestionService(_settingsService);
        _iconCacheService = new IconCacheService();
        _actionExecutor = new ActionExecutorService(_everythingService);

        _mainWindow = new MainWindow(
            _settingsService,
            _everythingService,
            _startMenuService,
            _webSuggestionService,
            _iconCacheService,
            _actionExecutor
        );
        MainWindow = _mainWindow;
        _mainWindow.PreWarm();

        _winKeyHookService = new WinKeyHookService(_settingsService);
        _winKeyHookService.SetMainWindowHandle(new System.Windows.Interop.WindowInteropHelper(_mainWindow).Handle);
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
        menu.Items.Add(new Forms.ToolStripSeparator());

        var directModeItem = new Forms.ToolStripMenuItem("单按 Win 键直接呼出搜索面板");
        var typeModeItem = new Forms.ToolStripMenuItem("按 Win 键打开开始菜单后打字接管");

        void SyncModeChecks()
        {
            if (_settingsService == null)
                return;
            directModeItem.Checked = _settingsService.Current.TriggerMode == WinKeyTriggerMode.DirectOnSingleWinTap;
            typeModeItem.Checked = _settingsService.Current.TriggerMode == WinKeyTriggerMode.InterceptTypingInStartMenu;
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

        menu.Opening += (_, _) => SyncModeChecks();
        menu.Items.Add(directModeItem);
        menu.Items.Add(typeModeItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 EverythingonStart", null, (_, _) => Shutdown());

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
        Task.Factory.StartNew(
            () =>
            {
                using var waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, ToggleEventName);
                while (true)
                {
                    waitHandle.WaitOne();
                    Dispatcher.BeginInvoke(() => _mainWindow?.ShowPopup(null));
                }
            },
            TaskCreationOptions.LongRunning
        );

        Task.Factory.StartNew(
            () =>
            {
                using var shutdownHandle = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
                shutdownHandle.WaitOne();
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }

                _winKeyHookService?.Dispose();
                _everythingService?.Dispose();
                Environment.Exit(0);
            },
            TaskCreationOptions.LongRunning
        );
    }

    private void RunSelfTestAndExit()
    {
        using var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var sw = Stopwatch.StartNew();
        var settings = new AppSettingsService();
        using var everything = new EverythingSearchService(settings.Current.EverythingInstanceName);
        var startMenu = new StartMenuIndexService();
        startMenu.RebuildAppIndex();
        var webService = new WebSuggestionService(settings);
        var iconService = new IconCacheService();
        var actionExecutor = new ActionExecutorService(everything);

        var window = new MainWindow(settings, everything, startMenu, webService, iconService, actionExecutor);
        window.PreWarm();
        window.RevealAndRaiseTaskbar();
        window.PositionLikeStartMenu();
        window.ResultsListBox.UpdateLayout();

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

        var querySw = Stopwatch.StartNew();
        var docQuery = everything.Query("rar", SearchCategory.Documents, 10);
        var folderQuery = everything.Query("Windows", SearchCategory.Folders, 5);
        querySw.Stop();

        var apps = startMenu.QueryApps("电脑", 5);
        var sysSettings = startMenu.QuerySettings("任务栏", 5);

        using var hook = new WinKeyHookService(settings);
        hook.Start();
        hook.Stop();

        sw.Stop();
        stdout.WriteLine($"[SelfTest] Engine: {everything.ActiveEngineDescription}");
        stdout.WriteLine($"[SelfTest] EverythingExePath: {actionExecutor.ResolveEverythingExecutablePath() ?? "Not Found"}");
        stdout.WriteLine($"[SelfTest] Everything Connected: {everything.IsConnected}");
        stdout.WriteLine($"[SelfTest] DocQuery('rar') Count={docQuery.TotalCount}, Returned={docQuery.Items.Count}");
        stdout.WriteLine($"[SelfTest] FolderQuery('Windows') Count={folderQuery.TotalCount}, Returned={folderQuery.Items.Count}");
        stdout.WriteLine($"[SelfTest] Everything Query Elapsed: {querySw.Elapsed.TotalMilliseconds:F2} ms");
        stdout.WriteLine($"[SelfTest] Apps('电脑') Returned={apps.Count}, Settings('任务栏') Returned={sysSettings.Count}");
        stdout.WriteLine($"[SelfTest] ShowInTaskbar={window.ShowInTaskbar}");
        stdout.WriteLine($"[SelfTest] WindowBounds=({window.Left:F1}, {window.Top:F1}, {window.Width:F1}x{window.Height:F1}), ScreenHeight={SystemParameters.PrimaryScreenHeight:F1}");
        stdout.WriteLine($"[SelfTest] FirstSelectableRowHeight={firstSelectableRowHeight:F1} px");
        stdout.WriteLine($"[SelfTest] Total SelfTest Elapsed: {sw.Elapsed.TotalMilliseconds:F2} ms");

        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _winKeyHookService?.Dispose();
        _everythingService?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
