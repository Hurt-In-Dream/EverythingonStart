using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EverythingonStart.Models;
using EverythingonStart.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace EverythingonStart;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private readonly EverythingSearchService _everythingService;
    private readonly StartMenuBoundsService _boundsService;
    private readonly Action _onSettingsApplied;
    private readonly AppThemeService _themeService;
    private readonly WindowBackdropService _backdropService;
    private bool _isLoading;

    public SettingsWindow(
        AppSettingsService settingsService,
        EverythingSearchService everythingService,
        StartMenuBoundsService boundsService,
        Action onSettingsApplied,
        int initialTabIndex = 0
    )
    {
        _settingsService = settingsService;
        _everythingService = everythingService;
        _boundsService = boundsService;
        _onSettingsApplied = onSettingsApplied;

        _themeService = AppThemeService.GetOrCreate(_settingsService);
        InitializeComponent();
        _themeService.RegisterWindow(this);
        _backdropService = new WindowBackdropService(this, SettingsGaussianBackdrop, _settingsService);
        LoadFromSettings();

        switch (initialTabIndex)
        {
            case 1:
                NavTabSortAndSearch.IsChecked = true;
                break;
            case 2:
                NavTabInteraction.IsChecked = true;
                break;
            case 3:
                NavTabAppearance.IsChecked = true;
                break;
            default:
                NavTabFilters.IsChecked = true;
                break;
        }
    }

    private void LoadFromSettings()
    {
        _isLoading = true;
        var cur = _settingsService.Current;

        CmbGlobalFilterMode.SelectedIndex = (int)cur.FilterMode;
        ChkUsePerCategoryMode.IsChecked = cur.UsePerCategoryFilterMode;
        CmbAppsFilterMode.SelectedIndex = (int)cur.AppsFilterMode;
        CmbDocsFilterMode.SelectedIndex = (int)cur.DocumentsFilterMode;
        CmbFoldersFilterMode.SelectedIndex = (int)cur.FoldersFilterMode;
        CmbPhotosFilterMode.SelectedIndex = (int)cur.PhotosFilterMode;

        TxtCustomAppsFilter.Text = cur.CustomAppsFilter;
        ChkIncludeStartMenuApps.IsChecked = cur.IncludeStartMenuApps;
        ChkIncludeEverythingApps.IsChecked = cur.IncludeEverythingApps;

        ChkDefaultDocsMatchAll.IsChecked = cur.DefaultDocumentsMatchAllFiles;
        TxtCustomDocsFilter.Text = cur.CustomDocumentsFilter;
        TxtCustomFoldersFilter.Text = cur.CustomFoldersFilter;
        TxtCustomPhotosFilter.Text = cur.CustomPhotosFilter;

        TxtCustomAudioFilter.Text = cur.CustomAudioFilter;
        TxtCustomVideoFilter.Text = cur.CustomVideoFilter;
        TxtCustomCompressedFilter.Text = cur.CustomCompressedFilter;
        TxtCustomCodeFilter.Text = cur.CustomCodeFilter;

        CmbSortOrder.SelectedIndex = (int)cur.SortOrder;
        CmbExtraTypeFilter.SelectedIndex = (int)cur.ActiveExtraFilter;
        ChkFollowEverythingFlags.IsChecked = cur.FollowEverythingSearchFlags;
        ChkMatchCase.IsChecked = cur.MatchCase;
        ChkMatchWholeWord.IsChecked = cur.MatchWholeWord;
        ChkMatchPath.IsChecked = cur.MatchPath;
        ChkEnableRegex.IsChecked = cur.EnableRegex;

        SldMaxPerGroup.Value = Math.Clamp(cur.MaxResultsPerGroupInAll, 3, 15);
        TxtMaxPerGroupVal.Text = $"{(int)SldMaxPerGroup.Value}";
        SldMaxSingleCategory.Value = Math.Clamp(cur.MaxResultsInSingleCategory, 30, 500);
        TxtMaxSingleCategoryVal.Text = $"{(int)SldMaxSingleCategory.Value}";

        CmbWebSearchEngine.SelectedIndex = (int)cur.SearchEngine;
        ChkShowWebInAll.IsChecked = cur.ShowWebSuggestionsInAll;
        ChkShowSettingsInAll.IsChecked = cur.ShowSettingsInAll;

        CmbTriggerMode.SelectedIndex = (int)cur.TriggerMode;
        int initialItemIdx = (int)cur.InitialSearchItem;
        CmbInitialSearchItem.SelectedIndex = initialItemIdx;
        CmbInitialSearchItemInFilters.SelectedIndex = initialItemIdx;
        ChkBackButtonClosesOnAll.IsChecked = cur.BackButtonClosesOnAllTab;
        ChkKeepTaskbarInFullscreen.IsChecked = cur.KeepTaskbarVisibleInFullscreen;
        ChkEscClearsFirst.IsChecked = cur.EscClearsSearchFirst;
        ChkAutoHideOnDeactivate.IsChecked = cur.AutoHideOnDeactivate;
        ChkClearSearchOnOpen.IsChecked = cur.ClearSearchOnOpen;

        ChkAutoScaleStartMenu.IsChecked = cur.AutoScaleToStartMenu;
        SldManualScale.Value = Math.Clamp(Math.Round(cur.ManualUiScale * 100.0), 75, 125);
        TxtManualScaleVal.Text = $"{(int)SldManualScale.Value}%";
        SldManualScale.IsEnabled = cur.AutoScaleToStartMenu != true;
        TxtCurrentBoundsSource.Text = $"当前尺寸来源：{_boundsService.LastPlacementSource}";

        SldWindowOpacity.Value = Math.Clamp(cur.WindowOpacityPercent, 75, 100);
        TxtWindowOpacityVal.Text = $"{(int)SldWindowOpacity.Value}%";
        ChkEnablePopupAnim.IsChecked = cur.EnablePopupAnimation;
        CmbThemeMode.SelectedIndex = Enum.IsDefined(cur.ThemeMode) ? (int)cur.ThemeMode : (int)AppThemeMode.Dark;
        ChkUseSystemAccentColor.IsChecked = cur.UseSystemAccentColor;
        ChkEnableGaussianBlur.IsChecked = cur.EnableGaussianBlur;
        SldGaussianBlurRadius.Value = Math.Clamp(cur.GaussianBlurRadius, 6, 40);
        TxtGaussianBlurRadiusVal.Text = $"{(int)SldGaussianBlurRadius.Value}";
        SldGaussianBlurRadius.IsEnabled = cur.EnableGaussianBlur;

        ChkAutostart.IsChecked = _settingsService.IsAutostartEnabled();
        TxtInstanceName.Text = cur.EverythingInstanceName;
        TxtEverythingExePath.Text = cur.CustomEverythingExecutablePath;
        TxtFiltersCsvPath.Text = cur.CustomEverythingFiltersCsvPath;

        _isLoading = false;
        SyncPerCategoryComboEnabledState();
        SyncFollowFlagsEnabledState();
        UpdateCategoryFilterPreviews();
    }

    private void SyncPerCategoryComboEnabledState()
    {
        bool perCat = ChkUsePerCategoryMode.IsChecked == true;
        CmbAppsFilterMode.IsEnabled = perCat;
        CmbDocsFilterMode.IsEnabled = perCat;
        CmbFoldersFilterMode.IsEnabled = perCat;
        CmbPhotosFilterMode.IsEnabled = perCat;

        if (!perCat && CmbGlobalFilterMode.SelectedIndex >= 0)
        {
            int idx = CmbGlobalFilterMode.SelectedIndex;
            CmbAppsFilterMode.SelectedIndex = idx;
            CmbDocsFilterMode.SelectedIndex = idx;
            CmbFoldersFilterMode.SelectedIndex = idx;
            CmbPhotosFilterMode.SelectedIndex = idx;
        }
    }

    private void SyncFollowFlagsEnabledState()
    {
        bool manualFlags = ChkFollowEverythingFlags.IsChecked != true;
        ChkMatchCase.IsEnabled = manualFlags;
        ChkMatchWholeWord.IsEnabled = manualFlags;
        ChkMatchPath.IsEnabled = manualFlags;
        ChkEnableRegex.IsEnabled = manualFlags;
    }

    private void UpdateCategoryFilterPreviews()
    {
        if (!IsInitialized)
            return;

        TxtDetectedEverythingConfig.Text = $"Everything 配置状态：{_everythingService.DetectedConfigSummary}";

        var appsMode = GetUiCategoryMode(CmbAppsFilterMode);
        var docsMode = GetUiCategoryMode(CmbDocsFilterMode);
        var foldersMode = GetUiCategoryMode(CmbFoldersFilterMode);
        var photosMode = GetUiCategoryMode(CmbPhotosFilterMode);

        TxtAppsPreview.Text = $"当前生效查询前缀：{BuildPreviewPrefix(SearchCategory.Apps, appsMode, TxtCustomAppsFilter.Text, false)}";
        TxtDocsPreview.Text = $"当前生效查询前缀：{BuildPreviewPrefix(SearchCategory.Documents, docsMode, TxtCustomDocsFilter.Text, ChkDefaultDocsMatchAll.IsChecked == true)}";
        TxtFoldersPreview.Text = $"当前生效查询前缀：{BuildPreviewPrefix(SearchCategory.Folders, foldersMode, TxtCustomFoldersFilter.Text, false)}";
        TxtPhotosPreview.Text = $"当前生效查询前缀：{BuildPreviewPrefix(SearchCategory.Photos, photosMode, TxtCustomPhotosFilter.Text, false)}";
    }

    private FilterConfigMode GetUiCategoryMode(ComboBox categoryCombo)
    {
        int index = ChkUsePerCategoryMode.IsChecked == true
            ? categoryCombo.SelectedIndex
            : CmbGlobalFilterMode.SelectedIndex;
        return index is >= 0 and <= 2 ? (FilterConfigMode)index : FilterConfigMode.Default;
    }

    private static string BuildPreviewPrefix(SearchCategory category, FilterConfigMode mode, string customText, bool defaultDocsMatchAll)
    {
        return category switch
        {
            SearchCategory.Apps => mode switch
            {
                FilterConfigMode.Custom => EverythingSearchService.FormatRuleOrExtensionList(customText, isFolderCategory: false),
                FilterConfigMode.FollowEverything => $"!folder: ext:{AppSettings.EverythingNativeExecutableExtensions}",
                _ => EverythingSearchService.FormatRuleOrExtensionList(AppSettings.DefaultAppsExtensions, isFolderCategory: false)
            },
            SearchCategory.Documents => mode switch
            {
                FilterConfigMode.Custom => EverythingSearchService.FormatRuleOrExtensionList(customText, isFolderCategory: false),
                FilterConfigMode.FollowEverything => $"!folder: ext:{AppSettings.EverythingNativeDocumentExtensions}",
                _ => defaultDocsMatchAll
                    ? "!folder: (所有非文件夹文件)"
                    : EverythingSearchService.FormatRuleOrExtensionList(AppSettings.DefaultDocumentsExtensions, isFolderCategory: false)
            },
            SearchCategory.Folders => mode switch
            {
                FilterConfigMode.Custom => EverythingSearchService.FormatRuleOrExtensionList(customText, isFolderCategory: true),
                _ => "folder:"
            },
            SearchCategory.Photos => mode switch
            {
                FilterConfigMode.Custom => EverythingSearchService.FormatRuleOrExtensionList(customText, isFolderCategory: false),
                FilterConfigMode.FollowEverything => $"!folder: ext:{AppSettings.EverythingNativePictureExtensions}",
                _ => EverythingSearchService.FormatRuleOrExtensionList(AppSettings.DefaultPhotosExtensions, isFolderCategory: false)
            },
            _ => string.Empty
        };
    }

    private void OnNavTabChecked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
            return;

        PageFilters.Visibility = NavTabFilters.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSortAndSearch.Visibility = NavTabSortAndSearch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageInteraction.Visibility = NavTabInteraction.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAppearance.Visibility = NavTabAppearance.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsScrollViewer?.ScrollToTop();
    }

    private void OnGlobalFilterModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading)
            return;

        SyncPerCategoryComboEnabledState();
        UpdateCategoryFilterPreviews();
    }

    private void OnInitialSearchItemComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || CmbInitialSearchItem == null || CmbInitialSearchItemInFilters == null)
            return;

        _isLoading = true;
        try
        {
            if (ReferenceEquals(sender, CmbInitialSearchItem))
            {
                CmbInitialSearchItemInFilters.SelectedIndex = CmbInitialSearchItem.SelectedIndex;
            }
            else if (ReferenceEquals(sender, CmbInitialSearchItemInFilters))
            {
                CmbInitialSearchItem.SelectedIndex = CmbInitialSearchItemInFilters.SelectedIndex;
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void OnPerCategoryModeToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
            return;

        SyncPerCategoryComboEnabledState();
        UpdateCategoryFilterPreviews();
    }

    private void OnFilterPreviewNeedsUpdate(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
            return;

        UpdateCategoryFilterPreviews();
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading)
            return;

        UpdateCategoryFilterPreviews();
    }

    private void OnFollowFlagsChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
            return;

        SyncFollowFlagsEnabledState();
    }

    private void OnAutoScaleToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading || SldManualScale == null)
            return;

        SldManualScale.IsEnabled = ChkAutoScaleStartMenu.IsChecked != true;
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized)
            return;

        if (ReferenceEquals(sender, SldMaxPerGroup) && TxtMaxPerGroupVal != null)
            TxtMaxPerGroupVal.Text = $"{(int)SldMaxPerGroup.Value}";
        else if (ReferenceEquals(sender, SldMaxSingleCategory) && TxtMaxSingleCategoryVal != null)
            TxtMaxSingleCategoryVal.Text = $"{(int)SldMaxSingleCategory.Value}";
        else if (ReferenceEquals(sender, SldManualScale) && TxtManualScaleVal != null)
            TxtManualScaleVal.Text = $"{(int)SldManualScale.Value}%";
        else if (ReferenceEquals(sender, SldWindowOpacity) && TxtWindowOpacityVal != null)
            TxtWindowOpacityVal.Text = $"{(int)SldWindowOpacity.Value}%";
        else if (ReferenceEquals(sender, SldGaussianBlurRadius) && TxtGaussianBlurRadiusVal != null)
            TxtGaussianBlurRadiusVal.Text = $"{(int)SldGaussianBlurRadius.Value}";
    }

    private void OnReprobeStartMenuSizeClick(object sender, RoutedEventArgs e)
    {
        var cur = _settingsService.Current;
        cur.CachedStartMenuCardWidthDip = 0;
        cur.CachedStartMenuCardHeightDip = 0;
        _boundsService.TryCaptureOpenStartMenuBounds();
        var placement = _boundsService.CalculatePlacement();
        TxtCurrentBoundsSource.Text = $"当前尺寸来源：{placement.SourceDescription}";
        HeaderStatusText.Text = $"已更新窗口尺寸：{placement.CardWidthDip:F0}×{placement.CardHeightDip:F0}";
    }

    private void OnBrowseEverythingExeClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 Everything.exe 主程序",
            Filter = "Everything 主程序 (Everything*.exe)|Everything*.exe|所有可执行文件 (*.exe)|*.exe"
        };
        if (dlg.ShowDialog(this) == true)
            TxtEverythingExePath.Text = dlg.FileName;
    }

    private void OnBrowseFiltersCsvClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 Everything 筛选器配置文件 (Filters.csv)",
            Filter = "Everything 筛选器文件 (Filters*.csv)|Filters*.csv|CSV 文件 (*.csv)|*.csv"
        };
        if (dlg.ShowDialog(this) == true)
            TxtFiltersCsvPath.Text = dlg.FileName;
    }

    private void OnResetDefaultFiltersClick(object sender, RoutedEventArgs e)
    {
        TxtCustomAppsFilter.Text = AppSettings.DefaultAppsExtensions;
        TxtCustomDocsFilter.Text = AppSettings.DefaultDocumentsExtensions;
        ChkDefaultDocsMatchAll.IsChecked = true;
        TxtCustomFoldersFilter.Text = AppSettings.DefaultFoldersFilter;
        TxtCustomPhotosFilter.Text = AppSettings.DefaultPhotosExtensions;
        TxtCustomAudioFilter.Text = AppSettings.DefaultAudioExtensions;
        TxtCustomVideoFilter.Text = AppSettings.DefaultVideoExtensions;
        TxtCustomCompressedFilter.Text = AppSettings.DefaultCompressedExtensions;
        TxtCustomCodeFilter.Text = AppSettings.DefaultCodeExtensions;
        UpdateCategoryFilterPreviews();
        HeaderStatusText.Text = "已恢复默认文件类型扩展名列表";
    }

    private void OnResyncEverythingConfigClick(object sender, RoutedEventArgs e)
    {
        _settingsService.Current.CustomEverythingFiltersCsvPath = TxtFiltersCsvPath.Text.Trim();
        _everythingService.RefreshEverythingExternalConfig();
        UpdateCategoryFilterPreviews();
        HeaderStatusText.Text = $"已重新同步 Everything 配置 ({_everythingService.DetectedConfigSummary})";
    }

    private void OnSaveAndApplyClick(object sender, RoutedEventArgs e)
    {
        var cur = _settingsService.Current;
        ApplyThemeSelections();

        cur.FilterMode = (FilterConfigMode)Math.Clamp(CmbGlobalFilterMode.SelectedIndex, 0, 2);
        cur.UsePerCategoryFilterMode = ChkUsePerCategoryMode.IsChecked == true;
        cur.AppsFilterMode = (FilterConfigMode)Math.Clamp(CmbAppsFilterMode.SelectedIndex, 0, 2);
        cur.DocumentsFilterMode = (FilterConfigMode)Math.Clamp(CmbDocsFilterMode.SelectedIndex, 0, 2);
        cur.FoldersFilterMode = (FilterConfigMode)Math.Clamp(CmbFoldersFilterMode.SelectedIndex, 0, 2);
        cur.PhotosFilterMode = (FilterConfigMode)Math.Clamp(CmbPhotosFilterMode.SelectedIndex, 0, 2);

        cur.CustomAppsFilter = TxtCustomAppsFilter.Text.Trim();
        cur.IncludeStartMenuApps = ChkIncludeStartMenuApps.IsChecked == true;
        cur.IncludeEverythingApps = ChkIncludeEverythingApps.IsChecked == true;

        cur.DefaultDocumentsMatchAllFiles = ChkDefaultDocsMatchAll.IsChecked == true;
        cur.CustomDocumentsFilter = TxtCustomDocsFilter.Text.Trim();
        cur.CustomFoldersFilter = TxtCustomFoldersFilter.Text.Trim();
        cur.CustomPhotosFilter = TxtCustomPhotosFilter.Text.Trim();

        cur.CustomAudioFilter = TxtCustomAudioFilter.Text.Trim();
        cur.CustomVideoFilter = TxtCustomVideoFilter.Text.Trim();
        cur.CustomCompressedFilter = TxtCustomCompressedFilter.Text.Trim();
        cur.CustomCodeFilter = TxtCustomCodeFilter.Text.Trim();

        cur.SortOrder = (EverythingSortOrder)Math.Clamp(CmbSortOrder.SelectedIndex, 0, 13);
        cur.ActiveExtraFilter = (ExtraTypeFilter)Math.Clamp(CmbExtraTypeFilter.SelectedIndex, 0, 5);
        cur.FollowEverythingSearchFlags = ChkFollowEverythingFlags.IsChecked == true;
        cur.MatchCase = ChkMatchCase.IsChecked == true;
        cur.MatchWholeWord = ChkMatchWholeWord.IsChecked == true;
        cur.MatchPath = ChkMatchPath.IsChecked == true;
        cur.EnableRegex = ChkEnableRegex.IsChecked == true;

        cur.MaxResultsPerGroupInAll = (int)Math.Round(SldMaxPerGroup.Value);
        cur.MaxResultsInSingleCategory = (int)Math.Round(SldMaxSingleCategory.Value);
        cur.SearchEngine = (WebSearchEngine)Math.Clamp(CmbWebSearchEngine.SelectedIndex, 0, 3);
        cur.ShowWebSuggestionsInAll = ChkShowWebInAll.IsChecked == true;
        cur.ShowSettingsInAll = ChkShowSettingsInAll.IsChecked == true;

        cur.TriggerMode = (WinKeyTriggerMode)Math.Clamp(CmbTriggerMode.SelectedIndex, 0, 1);
        cur.InitialSearchItem = (InitialSearchItemMode)Math.Clamp(CmbInitialSearchItem.SelectedIndex, 0, 1);
        cur.BackButtonClosesOnAllTab = ChkBackButtonClosesOnAll.IsChecked == true;
        cur.KeepTaskbarVisibleInFullscreen = ChkKeepTaskbarInFullscreen.IsChecked == true;
        cur.EscClearsSearchFirst = ChkEscClearsFirst.IsChecked == true;
        cur.AutoHideOnDeactivate = ChkAutoHideOnDeactivate.IsChecked == true;
        cur.ClearSearchOnOpen = ChkClearSearchOnOpen.IsChecked == true;

        cur.AutoScaleToStartMenu = ChkAutoScaleStartMenu.IsChecked == true;
        cur.ManualUiScale = Math.Round(SldManualScale.Value / 100.0, 2);
        cur.WindowOpacityPercent = Math.Round(SldWindowOpacity.Value);
        cur.EnablePopupAnimation = ChkEnablePopupAnim.IsChecked == true;
        ApplyGaussianBlurSelections();

        var newInstanceName = TxtInstanceName.Text.Trim();
        bool instanceChanged = !string.Equals(cur.EverythingInstanceName, newInstanceName, StringComparison.Ordinal);
        cur.EverythingInstanceName = newInstanceName;
        cur.CustomEverythingExecutablePath = TxtEverythingExePath.Text.Trim();
        cur.CustomEverythingFiltersCsvPath = TxtFiltersCsvPath.Text.Trim();

        _settingsService.Save();
        _settingsService.SetAutostart(ChkAutostart.IsChecked == true);

        if (instanceChanged)
            _everythingService.SetInstanceName(cur.EverythingInstanceName);
        _everythingService.RefreshEverythingExternalConfig();

        _themeService.ApplyCurrentSettings();
        _onSettingsApplied();
        Close();
    }

    private void ApplyThemeSelections()
    {
        _settingsService.Current.ThemeMode = (AppThemeMode)Math.Clamp(CmbThemeMode.SelectedIndex, 0, 2);
        _settingsService.Current.UseSystemAccentColor = ChkUseSystemAccentColor.IsChecked == true;
    }

    private void ApplyGaussianBlurSelections()
    {
        _settingsService.Current.EnableGaussianBlur = ChkEnableGaussianBlur.IsChecked == true;
        _settingsService.Current.GaussianBlurRadius = (int)Math.Clamp(Math.Round(SldGaussianBlurRadius.Value), 6, 40);
    }

    private void OnGaussianBlurOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_isLoading && SldGaussianBlurRadius != null)
            SldGaussianBlurRadius.IsEnabled = ChkEnableGaussianBlur.IsChecked == true;
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
