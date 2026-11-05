using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using EverythingonStart.Models;
using EverythingonStart.Services;
using Microsoft.Win32;

namespace EverythingonStart;

public partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly AppSettingsService _settingsService;
    private readonly EverythingSearchService _everythingService;
    private readonly StartMenuIndexService _startMenuService;
    private readonly WebSuggestionService _webSuggestionService;
    private readonly IconCacheService _iconCacheService;
    private readonly ActionExecutorService _actionExecutor;

    private readonly ObservableCollection<SearchListEntry> _entries = [];
    private CancellationTokenSource? _searchCts;
    private SearchCategory _currentCategory = SearchCategory.All;
    private SearchResultItem? _selectedItem;
    private bool _suppressDeactivateHide;
    private int _webInsertStartIndex = -1;
    private int _webInsertedCount;

    public MainWindow(
        AppSettingsService settingsService,
        EverythingSearchService everythingService,
        StartMenuIndexService startMenuService,
        WebSuggestionService webSuggestionService,
        IconCacheService iconCacheService,
        ActionExecutorService actionExecutor
    )
    {
        _settingsService = settingsService;
        _everythingService = everythingService;
        _startMenuService = startMenuService;
        _webSuggestionService = webSuggestionService;
        _iconCacheService = iconCacheService;
        _actionExecutor = actionExecutor;

        InitializeComponent();
        ResultsListBox.ItemsSource = _entries;

        UpdateEngineBadge();
        PopulateDefaultLandingEntries();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
            SetWindowLongW(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
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
        PositionLikeStartMenu();
        UpdateEngineBadge();

        if (!IsVisible)
            Show();

        Opacity = 1.0;
        ActivateWindowForcefully();

        if (!string.IsNullOrEmpty(initialText))
        {
            SearchTextBox.Text = initialText;
            SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
        }
        else
        {
            SearchTextBox.SelectAll();
        }

        SearchTextBox.Focus();
        Keyboard.Focus(SearchTextBox);

        if (string.IsNullOrWhiteSpace(SearchTextBox.Text))
            PopulateDefaultLandingEntries();
    }

    public void HidePopup()
    {
        if (!IsVisible)
            return;

        _searchCts?.Cancel();
        Hide();
    }

    private void PositionLikeStartMenu()
    {
        var workArea = SystemParameters.WorkArea;
        bool centerAligned = IsTaskbarCentered();

        if (centerAligned)
            Left = workArea.Left + (workArea.Width - Width) / 2.0;
        else
            Left = workArea.Left + 12;

        Top = Math.Max(workArea.Top + 12, workArea.Bottom - Height - 6);
    }

    private static bool IsTaskbarCentered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        var val = key?.GetValue("TaskbarAl");
        return val is int intVal ? intVal != 0 : true;
    }

    private void ActivateWindowForcefully()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var fg = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;

        bool attached = false;
        if (fgThread != 0 && fgThread != currentThread)
            attached = AttachThreadInput(fgThread, currentThread, true);

        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);

        if (attached)
            AttachThreadInput(fgThread, currentThread, false);

        Activate();
    }

    private void UpdateEngineBadge()
    {
        _everythingService.EnsureConnected();
        EngineStatusBadge.Text = _everythingService.IsConnected ? "Everything 极速引擎" : "Everything 未运行";
        EngineStatusBadge.ToolTip = _everythingService.ActiveEngineDescription;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        bool hasText = !string.IsNullOrEmpty(SearchTextBox.Text);
        PlaceholderText.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        ClearSearchButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;

        TriggerSearch();
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Clear();
        SearchTextBox.Focus();
    }

    private void OnBackToAllCategoryClick(object sender, RoutedEventArgs e)
    {
        TabAll.IsChecked = true;
        SearchTextBox.Focus();
    }

    private void OnCategoryTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: SearchCategory category } && IsLoaded)
        {
            _currentCategory = category;
            TriggerSearch();
            SearchTextBox.Focus();
        }
    }

    private void TriggerSearch()
    {
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

        Task.Run(() => ExecuteLocalSearch(query, category, token), token);
    }

    private void PopulateDefaultLandingEntries()
    {
        _entries.Clear();
        _webInsertStartIndex = -1;
        _webInsertedCount = 0;

        var apps = _startMenuService.QueryApps("", 6);
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

        var settings = _startMenuService.QuerySettings("", 5);
        if (settings.Count > 0)
        {
            _entries.Add(SearchListEntry.CreateHeader("快捷系统设置", $"{settings.Count}+", SearchCategory.Settings));
            foreach (var setting in settings)
            {
                _iconCacheService.PopulateIconsAsync(setting);
                _entries.Add(SearchListEntry.CreateItem(setting));
            }
        }

        SelectFirstSelectableEntry();
    }

    private void ExecuteLocalSearch(string query, SearchCategory category, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return;

        if (category == SearchCategory.All)
        {
            var appResults = _startMenuService.QueryApps(query, 4);
            var settingResults = _startMenuService.QuerySettings(query, 3);
            var folderResult = _everythingService.Query(query, SearchCategory.Folders, 5, token);
            var docResult = _everythingService.Query(query, SearchCategory.Documents, 8, token);

            if (token.IsCancellationRequested)
                return;

            Dispatcher.Invoke(() =>
            {
                if (token.IsCancellationRequested)
                    return;

                BuildAllCategoryList(query, appResults, settingResults, folderResult, docResult);
            });

            _ = LoadAndSpliceWebSuggestionsAsync(query, maxCount: 4, token);
        }
        else if (category == SearchCategory.Web)
        {
            var directWeb = _webSuggestionService.CreateDirectWebSearchItem(query);
            _iconCacheService.PopulateIconsAsync(directWeb);

            Dispatcher.Invoke(() =>
            {
                if (token.IsCancellationRequested)
                    return;

                _entries.Clear();
                _entries.Add(SearchListEntry.CreateHeader("搜索 Web", "实时联想"));
                _entries.Add(SearchListEntry.CreateItem(directWeb, isBestMatch: true));
                _webInsertStartIndex = _entries.Count;
                _webInsertedCount = 0;
                SelectFirstSelectableEntry();
            });

            _ = LoadAndSpliceWebSuggestionsAsync(query, maxCount: 12, token);
        }
        else if (category == SearchCategory.Apps)
        {
            var localApps = _startMenuService.QueryApps(query, 40);
            var exeResult = _everythingService.Query(query, SearchCategory.Apps, 60, token);
            if (token.IsCancellationRequested)
                return;

            Dispatcher.Invoke(() =>
            {
                if (token.IsCancellationRequested)
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
            });
        }
        else if (category == SearchCategory.Settings)
        {
            var settings = _startMenuService.QuerySettings(query, 35);
            Dispatcher.Invoke(() =>
            {
                if (token.IsCancellationRequested)
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
            });
        }
        else
        {
            var evResult = _everythingService.Query(query, category, 120, token);
            if (token.IsCancellationRequested)
                return;

            string headerName = category switch
            {
                SearchCategory.Documents => "文档与文件",
                SearchCategory.Folders => "文件夹",
                SearchCategory.Photos => "照片与图像",
                _ => "搜索结果"
            };

            Dispatcher.Invoke(() =>
            {
                if (token.IsCancellationRequested)
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
            });
        }
    }

    private void BuildAllCategoryList(
        string query,
        IReadOnlyList<SearchResultItem> appResults,
        IReadOnlyList<SearchResultItem> settingResults,
        EverythingQueryResult folderResult,
        EverythingQueryResult docResult
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

        // 搜索 Web 分组（完全对应截图位置）
        var directWeb = _webSuggestionService.CreateDirectWebSearchItem(query);
        _iconCacheService.PopulateIconsAsync(directWeb);
        _entries.Add(SearchListEntry.CreateHeader("搜索 Web", "10+", SearchCategory.Web));
        _entries.Add(SearchListEntry.CreateItem(directWeb, isBestMatch: bestMatch == null));
        _webInsertStartIndex = _entries.Count;
        _webInsertedCount = 0;

        // 文件夹分组
        var folders = folderResult.Items.Where(f => !usedPaths.Contains(f.FullPath)).Take(4).ToList();
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
        var docs = docResult.Items.Where(d => !usedPaths.Contains(d.FullPath)).Take(6).ToList();
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
        var remainingApps = appResults.Where(a => !usedPaths.Contains(a.FullPath)).Take(3).ToList();
        if (remainingApps.Count > 0)
        {
            _entries.Add(SearchListEntry.CreateHeader("应用", $"{appResults.Count}", SearchCategory.Apps));
            foreach (var app in remainingApps)
            {
                _iconCacheService.PopulateIconsAsync(app);
                _entries.Add(SearchListEntry.CreateItem(app));
            }
        }

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
            if (!string.IsNullOrEmpty(SearchTextBox.Text))
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

    private void SelectCategoryTab(SearchCategory category)
    {
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
        target.IsChecked = true;
    }

    private void OnOptionsMenuButtonClick(object sender, RoutedEventArgs e)
    {
        _suppressDeactivateHide = true;

        var menu = new ContextMenu();

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
        exitItem.Click += (_, _) => Application.Current.Shutdown();

        menu.Items.Add(modeDirectItem);
        menu.Items.Add(modeTypeItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(engineBing);
        menu.Items.Add(engineGoogle);
        menu.Items.Add(engineBaidu);
        menu.Items.Add(new Separator());
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

        HidePopup();
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

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
}
