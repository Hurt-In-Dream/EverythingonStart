using System.IO;
using System.Runtime.InteropServices;
using EverythingonStart.Models;
using Microsoft.VisualBasic.FileIO;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace EverythingonStart.Services;

public readonly record struct EverythingQueryResult(IReadOnlyList<SearchResultItem> Items, int TotalCount);

public sealed class EverythingSearchService : IDisposable
{
    private const int PathBufferLength = 4096;
    private const uint PropertyIdName = 0;
    private const uint PropertyIdPath = 1;
    private const uint PropertyIdSize = 2;
    private const uint PropertyIdExtension = 3;
    private const uint PropertyIdDateCreated = 4;
    private const uint PropertyIdDateModified = 5;
    private const uint PropertyIdRunCount = 10;
    private const uint PropertyIdPathAndName = 240;

    private readonly object _syncLock = new();
    private readonly AppSettingsService? _settingsService;
    private IntPtr _pipeClient;
    private bool _usePipeSdk;
    private bool _useIpcSdk;
    private bool _pipeFastRunCountSort;
    private bool _ipcFastRunCountSort;
    private string _activeEngineDescription = "未连接";
    private string _configuredInstanceName = string.Empty;

    private HashSet<string> _photoExtensions = ParseExtensionSet(AppSettings.DefaultPhotosExtensions);
    private HashSet<string> _appExtensions = ParseExtensionSet(AppSettings.DefaultAppsExtensions);

    private static readonly string PrebuiltDefaultAppsQuery = $"!folder: ext:{AppSettings.DefaultAppsExtensions}";
    private static readonly string PrebuiltDefaultDocsQuery = $"!folder: ext:{AppSettings.DefaultDocumentsExtensions}";
    private static readonly string PrebuiltDefaultPhotosQuery = $"!folder: ext:{AppSettings.DefaultPhotosExtensions}";
    private static readonly string PrebuiltDefaultAudioQuery = $"!folder: ext:{AppSettings.DefaultAudioExtensions}";
    private static readonly string PrebuiltDefaultVideoQuery = $"!folder: ext:{AppSettings.DefaultVideoExtensions}";
    private static readonly string PrebuiltDefaultCompressedQuery = $"!folder: ext:{AppSettings.DefaultCompressedExtensions}";
    private static readonly string PrebuiltDefaultCodeQuery = $"!folder: ext:{AppSettings.DefaultCodeExtensions}";

    // 从 Everything Filters.csv 与 Everything.ini 同步的缓存
    private string _everythingCsvAppsQuery = $"!folder: ext:{AppSettings.EverythingNativeExecutableExtensions}";
    private string _everythingCsvDocsQuery = $"!folder: ext:{AppSettings.EverythingNativeDocumentExtensions}";
    private string _everythingCsvFoldersQuery = "folder:";
    private string _everythingCsvPhotosQuery = $"!folder: ext:{AppSettings.EverythingNativePictureExtensions}";
    private string _everythingCsvAudioQuery = $"!folder: ext:{AppSettings.EverythingNativeAudioExtensions}";
    private string _everythingCsvVideoQuery = $"!folder: ext:{AppSettings.EverythingNativeVideoExtensions}";
    private string _everythingCsvCompressedQuery = $"!folder: ext:{AppSettings.EverythingNativeCompressedExtensions}";

    private EverythingSortOrder _everythingIniSortOrder = EverythingSortOrder.NameAscending;
    private bool _everythingIniMatchCase;
    private bool _everythingIniMatchWholeWord;
    private bool _everythingIniMatchPath;
    private bool _everythingIniMatchRegex;
    private string _detectedConfigSummary = "未加载外部配置";

    public string ActiveEngineDescription => _activeEngineDescription;

    public string DetectedConfigSummary => _detectedConfigSummary;

    public bool IsConnected => _usePipeSdk || _useIpcSdk;

    public EverythingSearchService(string instanceName = "", AppSettingsService? settingsService = null)
    {
        _configuredInstanceName = instanceName;
        _settingsService = settingsService;
        RefreshEverythingExternalConfig();
        EnsureConnected();
    }

    public void SetInstanceName(string instanceName)
    {
        lock (_syncLock)
        {
            _configuredInstanceName = instanceName.Trim();
            DisconnectPipeLocked();
            _usePipeSdk = false;
            _useIpcSdk = false;
            EnsureConnectedLocked();
        }
    }

    public bool EnsureConnected()
    {
        lock (_syncLock)
        {
            return EnsureConnectedLocked();
        }
    }

    private bool EnsureConnectedLocked()
    {
        if (_usePipeSdk && _pipeClient != IntPtr.Zero)
            return true;
        if (_useIpcSdk)
            return true;

        if (TryConnectPipeLocked())
        {
            _usePipeSdk = true;
            return true;
        }

        if (TryConnectIpcLocked())
        {
            _useIpcSdk = true;
            return true;
        }

        _activeEngineDescription = "未检测到运行中的 Everything 实例";
        return false;
    }

    private bool TryConnectPipeLocked()
    {
        string?[] candidates = string.IsNullOrEmpty(_configuredInstanceName)
            ? [null, "1.5a"]
            : [_configuredInstanceName, null, "1.5a"];

        foreach (var candidate in candidates)
        {
            var handle = Everything3_ConnectW(candidate);
            if (handle != IntPtr.Zero)
            {
                _pipeClient = handle;
                _pipeFastRunCountSort = Everything3_IsPropertyFastSort(handle, PropertyIdRunCount);
                uint major = Everything3_GetMajorVersion(handle);
                uint minor = Everything3_GetMinorVersion(handle);
                uint rev = Everything3_GetRevision(handle);
                var instanceLabel = string.IsNullOrEmpty(candidate) ? "默认实例" : candidate;
                _activeEngineDescription = $"Everything {major}.{minor}.{rev} ({instanceLabel} / 命名管道)";
                return true;
            }
        }

        return false;
    }

    private bool TryConnectIpcLocked()
    {
        const uint SortRunCountDescending = 19;
        string[] candidates = string.IsNullOrEmpty(_configuredInstanceName)
            ? ["", "1.5a"]
            : [_configuredInstanceName, "", "1.5a"];

        foreach (var candidate in candidates)
        {
            Everything_SetInstanceName(candidate);
            uint major = Everything_GetMajorVersion();
            if (major > 0)
            {
                _ipcFastRunCountSort = Everything_IsFastSort(SortRunCountDescending);
                uint minor = Everything_GetMinorVersion();
                uint rev = Everything_GetRevision();
                var instanceLabel = string.IsNullOrEmpty(candidate) ? "默认实例" : candidate;
                _activeEngineDescription = $"Everything {major}.{minor}.{rev} ({instanceLabel} / IPC)";
                return true;
            }
        }

        return false;
    }

    private void DisconnectPipeLocked()
    {
        if (_pipeClient != IntPtr.Zero)
        {
            Everything3_DestroyClient(_pipeClient);
            _pipeClient = IntPtr.Zero;
        }
    }

    public void RefreshEverythingExternalConfig()
    {
        var settings = _settingsService?.Current;
        if (settings != null)
        {
            var photoExtSource = settings.GetEffectiveCategoryMode(SearchCategory.Photos) switch
            {
                FilterConfigMode.Custom => settings.CustomPhotosFilter,
                FilterConfigMode.FollowEverything => AppSettings.EverythingNativePictureExtensions,
                _ => AppSettings.DefaultPhotosExtensions
            };
            var appExtSource = settings.GetEffectiveCategoryMode(SearchCategory.Apps) switch
            {
                FilterConfigMode.Custom => settings.CustomAppsFilter,
                FilterConfigMode.FollowEverything => AppSettings.EverythingNativeExecutableExtensions,
                _ => AppSettings.DefaultAppsExtensions
            };

            var parsedPhotoSet = ParseExtensionSet(photoExtSource);
            if (parsedPhotoSet.Count > 0)
                _photoExtensions = parsedPhotoSet;

            var parsedAppSet = ParseExtensionSet(appExtSource);
            if (parsedAppSet.Count > 0)
                _appExtensions = parsedAppSet;
        }

        // 重置为 Everything 官方默认定义，若检测到 Filters.csv 则覆盖
        _everythingCsvAppsQuery = $"!folder: ext:{AppSettings.EverythingNativeExecutableExtensions}";
        _everythingCsvDocsQuery = $"!folder: ext:{AppSettings.EverythingNativeDocumentExtensions}";
        _everythingCsvFoldersQuery = "folder:";
        _everythingCsvPhotosQuery = $"!folder: ext:{AppSettings.EverythingNativePictureExtensions}";
        _everythingCsvAudioQuery = $"!folder: ext:{AppSettings.EverythingNativeAudioExtensions}";
        _everythingCsvVideoQuery = $"!folder: ext:{AppSettings.EverythingNativeVideoExtensions}";
        _everythingCsvCompressedQuery = $"!folder: ext:{AppSettings.EverythingNativeCompressedExtensions}";

        var csvPath = ResolveEverythingFiltersCsvPath();
        var iniPath = ResolveEverythingIniPath();

        if (!string.IsNullOrEmpty(csvPath) && File.Exists(csvPath))
        {
            LoadFiltersCsv(csvPath);
        }

        if (!string.IsNullOrEmpty(iniPath) && File.Exists(iniPath))
        {
            LoadEverythingIni(iniPath);
        }

        var parts = new List<string>(2);
        parts.Add(!string.IsNullOrEmpty(csvPath) ? $"Filters.csv: {Path.GetFileName(csvPath)}" : "Filters.csv: 使用 Everything 原生内置筛选器");
        parts.Add(!string.IsNullOrEmpty(iniPath) ? $"INI: {iniPath}" : "INI: 未检测到");
        _detectedConfigSummary = string.Join(" | ", parts);
    }

    public string? ResolveEverythingFiltersCsvPath()
    {
        var custom = _settingsService?.Current.CustomEverythingFiltersCsvPath?.Trim();
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
            return custom;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] candidates =
        [
            Path.Combine(appData, "Everything", "Filters-1.5a.csv"),
            Path.Combine(appData, "Everything", "Filters.csv"),
            @"D:\Everything\Filters-1.5a.csv",
            @"D:\Everything\Filters.csv",
            @"C:\Program Files\Everything\Filters.csv",
            @"C:\Program Files\Everything 1.5a\Filters-1.5a.csv"
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    public string? ResolveEverythingIniPath()
    {
        var custom = _settingsService?.Current.CustomEverythingIniPath?.Trim();
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
            return custom;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] candidates =
        [
            @"D:\Everything\Everything.ini",
            @"D:\Everything\Everything-1.5a.ini",
            Path.Combine(appData, "Everything", "Everything-1.5a.ini"),
            Path.Combine(appData, "Everything", "Everything.ini"),
            @"C:\Program Files\Everything\Everything.ini"
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private void LoadFiltersCsv(string csvPath)
    {
        using var parser = new TextFieldParser(csvPath);
        parser.CommentTokens = ["#"];
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;

        if (parser.EndOfData)
            return;

        var headers = parser.ReadFields();
        if (headers == null)
            return;

        int nameIdx = Array.FindIndex(headers, h => h.Equals("Name", StringComparison.OrdinalIgnoreCase));
        int searchIdx = Array.FindIndex(headers, h => h.Equals("Search", StringComparison.OrdinalIgnoreCase));
        int macroIdx = Array.FindIndex(headers, h => h.Equals("Macro", StringComparison.OrdinalIgnoreCase));
        if (nameIdx < 0 || searchIdx < 0)
            return;

        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields == null || fields.Length <= Math.Max(nameIdx, searchIdx))
                continue;

            var name = fields[nameIdx].Trim();
            var search = fields[searchIdx].Trim();
            var macro = macroIdx >= 0 && macroIdx < fields.Length ? fields[macroIdx].Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(search))
                continue;

            if (name.Equals("EXECUTABLE", StringComparison.OrdinalIgnoreCase) || name.Contains("程序") || name.Contains("应用") || macro.Equals("exe", StringComparison.OrdinalIgnoreCase))
                _everythingCsvAppsQuery = FormatRuleOrExtensionList(search, isFolderCategory: false);
            else if (name.Equals("DOCUMENT", StringComparison.OrdinalIgnoreCase) || name.Contains("文档") || macro.Equals("doc", StringComparison.OrdinalIgnoreCase))
                _everythingCsvDocsQuery = FormatRuleOrExtensionList(search, isFolderCategory: false);
            else if (name.Equals("FOLDER", StringComparison.OrdinalIgnoreCase) || name.Contains("文件夹"))
                _everythingCsvFoldersQuery = FormatRuleOrExtensionList(search, isFolderCategory: true);
            else if (name.Equals("PICTURE", StringComparison.OrdinalIgnoreCase) || name.Equals("IMAGE", StringComparison.OrdinalIgnoreCase) || name.Contains("图片") || name.Contains("照片") || macro.Equals("pic", StringComparison.OrdinalIgnoreCase))
                _everythingCsvPhotosQuery = FormatRuleOrExtensionList(search, isFolderCategory: false);
            else if (name.Equals("AUDIO", StringComparison.OrdinalIgnoreCase) || name.Contains("音频") || macro.Equals("audio", StringComparison.OrdinalIgnoreCase))
                _everythingCsvAudioQuery = FormatRuleOrExtensionList(search, isFolderCategory: false);
            else if (name.Equals("VIDEO", StringComparison.OrdinalIgnoreCase) || name.Contains("视频") || macro.Equals("video", StringComparison.OrdinalIgnoreCase))
                _everythingCsvVideoQuery = FormatRuleOrExtensionList(search, isFolderCategory: false);
            else if (name.Equals("COMPRESSED", StringComparison.OrdinalIgnoreCase) || name.Contains("压缩") || macro.Equals("zip", StringComparison.OrdinalIgnoreCase))
                _everythingCsvCompressedQuery = FormatRuleOrExtensionList(search, isFolderCategory: false);
        }
    }

    private void LoadEverythingIni(string iniPath)
    {
        string sortName = "Name";
        bool sortAsc = true;

        foreach (var rawLine in File.ReadLines(iniPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '[')
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();

            if (key.Equals("sort", StringComparison.OrdinalIgnoreCase))
                sortName = val;
            else if (key.Equals("sort_ascending", StringComparison.OrdinalIgnoreCase))
                sortAsc = val != "0";
            else if (key.Equals("match_case", StringComparison.OrdinalIgnoreCase))
                _everythingIniMatchCase = val == "1";
            else if (key.Equals("match_whole_word", StringComparison.OrdinalIgnoreCase))
                _everythingIniMatchWholeWord = val == "1";
            else if (key.Equals("match_path", StringComparison.OrdinalIgnoreCase))
                _everythingIniMatchPath = val == "1";
            else if (key.Equals("match_regex", StringComparison.OrdinalIgnoreCase))
                _everythingIniMatchRegex = val == "1";
        }

        _everythingIniSortOrder = sortName.ToLowerInvariant() switch
        {
            "path" => sortAsc ? EverythingSortOrder.PathAscending : EverythingSortOrder.PathDescending,
            "size" => sortAsc ? EverythingSortOrder.SizeAscending : EverythingSortOrder.SizeDescending,
            "date modified" or "datemodified" => sortAsc ? EverythingSortOrder.DateModifiedAscending : EverythingSortOrder.DateModifiedDescending,
            "date created" or "datecreated" => sortAsc ? EverythingSortOrder.DateCreatedAscending : EverythingSortOrder.DateCreatedDescending,
            "extension" => EverythingSortOrder.ExtensionAscending,
            "run count" or "runcount" => EverythingSortOrder.RunCountDescending,
            _ => sortAsc ? EverythingSortOrder.NameAscending : EverythingSortOrder.NameDescending
        };
    }

    public static string FormatRuleOrExtensionList(string rawRule, bool isFolderCategory)
    {
        var trimmed = rawRule.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return isFolderCategory ? "folder:" : "!folder:";

        // 若已经包含 Everything 语法关键字（如 ext:, folder:, file:, !folder:），直接返回
        if (trimmed.Contains(':') || trimmed.StartsWith('!'))
        {
            if (!isFolderCategory && !trimmed.Contains("folder:", StringComparison.OrdinalIgnoreCase) && !trimmed.Contains("file:", StringComparison.OrdinalIgnoreCase))
                return $"!folder: {trimmed}";
            return trimmed;
        }

        // 将用户输入的分号、逗号或空格分隔的扩展名规范化为 ext:a;b;c
        var tokens = trimmed
            .Split([';', ',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.TrimStart('.', '*'))
            .Where(t => !string.IsNullOrWhiteSpace(t));

        var joined = string.Join(';', tokens);
        if (string.IsNullOrEmpty(joined))
            return isFolderCategory ? "folder:" : "!folder:";

        return isFolderCategory ? $"folder: {joined}" : $"!folder: ext:{joined}";
    }

    private static HashSet<string> ParseExtensionSet(string rawRule)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = rawRule;
        int extIndex = cleaned.IndexOf("ext:", StringComparison.OrdinalIgnoreCase);
        if (extIndex >= 0)
            cleaned = cleaned[(extIndex + 4)..];

        foreach (var token in cleaned.Split([';', ',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ext = token.TrimStart('.', '*');
            if (ext.Length > 0 && !ext.Contains(':'))
                set.Add($".{ext}");
        }

        return set;
    }

    public EverythingQueryResult Query(string rawSearchText, SearchCategory category, int maxResults, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawSearchText) || cancellationToken.IsCancellationRequested)
            return new EverythingQueryResult(Array.Empty<SearchResultItem>(), 0);

        var formattedQuery = BuildCategoryQuery(rawSearchText.Trim(), category);

        lock (_syncLock)
        {
            if (cancellationToken.IsCancellationRequested || !EnsureConnectedLocked())
                return new EverythingQueryResult(Array.Empty<SearchResultItem>(), 0);

            if (_usePipeSdk)
            {
                var result = QueryViaPipeLocked(formattedQuery, maxResults);
                if (result.HasValue)
                    return result.Value;

                DisconnectPipeLocked();
                _usePipeSdk = false;
                if (!EnsureConnectedLocked())
                    return new EverythingQueryResult(Array.Empty<SearchResultItem>(), 0);
            }

            if (_useIpcSdk)
                return QueryViaIpcLocked(formattedQuery, maxResults);

            return new EverythingQueryResult(Array.Empty<SearchResultItem>(), 0);
        }
    }

    public void IncrementRunCount(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return;

        lock (_syncLock)
        {
            if (!EnsureConnectedLocked())
                return;

            if (_usePipeSdk && _pipeClient != IntPtr.Zero)
                Everything3_IncRunCountFromFilenameW(_pipeClient, fullPath);
            else if (_useIpcSdk)
                Everything_IncRunCountFromFileName(fullPath);
        }
    }

    public string GetCategoryFilterPreview(SearchCategory category)
    {
        var settings = _settingsService?.Current;
        if (settings == null)
            return string.Empty;

        var mode = settings.GetEffectiveCategoryMode(category);
        return category switch
        {
            SearchCategory.Folders => mode switch
            {
                FilterConfigMode.Custom => FormatRuleOrExtensionList(settings.CustomFoldersFilter, isFolderCategory: true),
                FilterConfigMode.FollowEverything => _everythingCsvFoldersQuery,
                _ => AppSettings.DefaultFoldersFilter
            },
            SearchCategory.Documents => mode switch
            {
                FilterConfigMode.Custom => FormatRuleOrExtensionList(settings.CustomDocumentsFilter, isFolderCategory: false),
                FilterConfigMode.FollowEverything => _everythingCsvDocsQuery,
                _ => settings.DefaultDocumentsMatchAllFiles
                    ? "!folder:"
                    : PrebuiltDefaultDocsQuery
            },
            SearchCategory.Photos => mode switch
            {
                FilterConfigMode.Custom => FormatRuleOrExtensionList(settings.CustomPhotosFilter, isFolderCategory: false),
                FilterConfigMode.FollowEverything => _everythingCsvPhotosQuery,
                _ => PrebuiltDefaultPhotosQuery
            },
            SearchCategory.Apps => mode switch
            {
                FilterConfigMode.Custom => FormatRuleOrExtensionList(settings.CustomAppsFilter, isFolderCategory: false),
                FilterConfigMode.FollowEverything => _everythingCsvAppsQuery,
                _ => PrebuiltDefaultAppsQuery
            },
            _ => string.Empty
        };
    }

    public string BuildCategoryQuery(string searchText, SearchCategory category)
    {
        var (matchCase, matchWholeWord, _, enableRegex) = GetEffectiveMatchFlags();
        // Explicit case/word/regex rules must retain their original native semantics.
        if (!matchCase && !matchWholeWord && !enableRegex)
            searchText = PinyinSearchService.ExpandEverythingQuery(searchText);
        var prefix = GetCategoryFilterPreview(category);
        var extraPrefix = GetExtraFilterPrefix();

        if (!string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(extraPrefix))
            return $"{prefix} {extraPrefix} {searchText}";
        if (!string.IsNullOrEmpty(prefix))
            return $"{prefix} {searchText}";
        if (!string.IsNullOrEmpty(extraPrefix))
            return $"{extraPrefix} {searchText}";

        return searchText;
    }

    private string GetExtraFilterPrefix()
    {
        var settings = _settingsService?.Current;
        if (settings == null || settings.ActiveExtraFilter == ExtraTypeFilter.None)
            return string.Empty;

        var mode = settings.FilterMode;
        return settings.ActiveExtraFilter switch
        {
            ExtraTypeFilter.Audio => mode == FilterConfigMode.FollowEverything
                ? _everythingCsvAudioQuery
                : FormatRuleOrExtensionList(settings.CustomAudioFilter, isFolderCategory: false),
            ExtraTypeFilter.Video => mode == FilterConfigMode.FollowEverything
                ? _everythingCsvVideoQuery
                : FormatRuleOrExtensionList(settings.CustomVideoFilter, isFolderCategory: false),
            ExtraTypeFilter.Compressed => mode == FilterConfigMode.FollowEverything
                ? _everythingCsvCompressedQuery
                : FormatRuleOrExtensionList(settings.CustomCompressedFilter, isFolderCategory: false),
            ExtraTypeFilter.Executable => mode == FilterConfigMode.FollowEverything
                ? _everythingCsvAppsQuery
                : FormatRuleOrExtensionList(settings.CustomAppsFilter, isFolderCategory: false),
            ExtraTypeFilter.Code => FormatRuleOrExtensionList(settings.CustomCodeFilter, isFolderCategory: false),
            _ => string.Empty
        };
    }

    public EverythingSortOrder GetEffectiveSortOrder()
    {
        var configured = _settingsService?.Current.SortOrder ?? EverythingSortOrder.DefaultRelevance;
        return configured == EverythingSortOrder.FollowEverything ? _everythingIniSortOrder : configured;
    }

    public (bool MatchCase, bool MatchWholeWord, bool MatchPath, bool EnableRegex) GetEffectiveMatchFlags()
    {
        var settings = _settingsService?.Current;
        if (settings == null)
            return (false, false, false, false);

        if (settings.FollowEverythingSearchFlags)
            return (_everythingIniMatchCase, _everythingIniMatchWholeWord, _everythingIniMatchPath, _everythingIniMatchRegex);

        return (settings.MatchCase, settings.MatchWholeWord, settings.MatchPath, settings.EnableRegex);
    }

    private unsafe EverythingQueryResult? QueryViaPipeLocked(string queryText, int maxResults)
    {
        if (_pipeClient == IntPtr.Zero)
            return null;

        var searchState = Everything3_CreateSearchState();
        if (searchState == IntPtr.Zero)
            return null;

        IntPtr resultList = IntPtr.Zero;
        try
        {
            var (matchCase, matchWholeWord, matchPath, enableRegex) = GetEffectiveMatchFlags();
            var sortOrder = GetEffectiveSortOrder();

            Everything3_SetSearchTextW(searchState, queryText);
            Everything3_SetSearchMatchCase(searchState, matchCase);
            Everything3_SetSearchMatchPath(searchState, matchPath);
            Everything3_SetSearchMatchWholeWords(searchState, matchWholeWord);
            Everything3_SetSearchRegex(searchState, enableRegex);

            ApplyPipeSortOrder(searchState, sortOrder);

            Everything3_AddSearchPropertyRequest(searchState, PropertyIdPathAndName);
            Everything3_AddSearchPropertyRequest(searchState, PropertyIdName);
            Everything3_AddSearchPropertyRequest(searchState, PropertyIdPath);
            Everything3_AddSearchPropertyRequest(searchState, PropertyIdSize);
            Everything3_AddSearchPropertyRequest(searchState, PropertyIdDateModified);
            Everything3_SetSearchViewportOffset(searchState, 0);
            Everything3_SetSearchViewportCount(searchState, (nuint)maxResults);

            resultList = Everything3_Search(_pipeClient, searchState);
            if (resultList == IntPtr.Zero)
                return null;

            int totalCount = (int)Math.Min(Everything3_GetResultListCount(resultList), (nuint)int.MaxValue);
            int viewportCount = (int)Everything3_GetResultListViewportCount(resultList);
            var list = new List<SearchResultItem>(viewportCount);
            char* buffer = stackalloc char[PathBufferLength];

            for (nuint i = 0; (int)i < viewportCount; i++)
            {
                bool isFolder = Everything3_IsFolderResult(resultList, i);
                var len = Everything3_GetResultFullPathNameW(resultList, i, buffer, PathBufferLength);
                if (len == 0)
                    continue;

                var fullPath = new string(buffer, 0, (int)Math.Min(len, (nuint)(PathBufferLength - 1)));
                ulong rawSize = Everything3_GetResultSize(resultList, i);
                long size = rawSize == ulong.MaxValue ? -1 : (long)rawSize;
                ulong rawModified = Everything3_GetResultDateModified(resultList, i);
                DateTime? modified = ToDateTime(rawModified);

                list.Add(CreateResultItem(fullPath, isFolder, size, modified));
            }

            return new EverythingQueryResult(list, totalCount);
        }
        finally
        {
            if (resultList != IntPtr.Zero)
                Everything3_DestroyResultList(resultList);
            Everything3_DestroySearchState(searchState);
        }
    }

    private void ApplyPipeSortOrder(IntPtr searchState, EverythingSortOrder sortOrder)
    {
        switch (sortOrder)
        {
            case EverythingSortOrder.NameAscending:
                Everything3_AddSearchSort(searchState, PropertyIdName, ascending: true);
                break;
            case EverythingSortOrder.NameDescending:
                Everything3_AddSearchSort(searchState, PropertyIdName, ascending: false);
                break;
            case EverythingSortOrder.PathAscending:
                Everything3_AddSearchSort(searchState, PropertyIdPath, ascending: true);
                break;
            case EverythingSortOrder.PathDescending:
                Everything3_AddSearchSort(searchState, PropertyIdPath, ascending: false);
                break;
            case EverythingSortOrder.SizeDescending:
                Everything3_AddSearchSort(searchState, PropertyIdSize, ascending: false);
                break;
            case EverythingSortOrder.SizeAscending:
                Everything3_AddSearchSort(searchState, PropertyIdSize, ascending: true);
                break;
            case EverythingSortOrder.DateModifiedDescending:
                Everything3_AddSearchSort(searchState, PropertyIdDateModified, ascending: false);
                break;
            case EverythingSortOrder.DateModifiedAscending:
                Everything3_AddSearchSort(searchState, PropertyIdDateModified, ascending: true);
                break;
            case EverythingSortOrder.DateCreatedDescending:
                Everything3_AddSearchSort(searchState, PropertyIdDateCreated, ascending: false);
                break;
            case EverythingSortOrder.DateCreatedAscending:
                Everything3_AddSearchSort(searchState, PropertyIdDateCreated, ascending: true);
                break;
            case EverythingSortOrder.ExtensionAscending:
                Everything3_AddSearchSort(searchState, PropertyIdExtension, ascending: true);
                break;
            case EverythingSortOrder.RunCountDescending:
                Everything3_AddSearchSort(searchState, PropertyIdRunCount, ascending: false);
                break;
            default:
                Everything3_AddSearchSort(searchState, PropertyIdName, ascending: true);
                break;
        }
    }

    private unsafe EverythingQueryResult QueryViaIpcLocked(string queryText, int maxResults)
    {
        const uint RequestFullPathAndFileName = 0x00000004;
        const uint RequestSize = 0x00000010;
        const uint RequestDateModified = 0x00000040;

        var (matchCase, matchWholeWord, matchPath, enableRegex) = GetEffectiveMatchFlags();
        var sortOrder = GetEffectiveSortOrder();

        Everything_SetSearchW(queryText);
        Everything_SetRequestFlags(RequestFullPathAndFileName | RequestSize | RequestDateModified);
        Everything_SetMatchCase(matchCase);
        Everything_SetMatchPath(matchPath);
        Everything_SetMatchWholeWord(matchWholeWord);
        Everything_SetRegex(enableRegex);
        Everything_SetSort(MapIpcSortType(sortOrder));
        Everything_SetOffset(0);
        Everything_SetMax((uint)maxResults);

        if (!Everything_QueryW(true))
            return new EverythingQueryResult(Array.Empty<SearchResultItem>(), 0);

        try
        {
            int totalCount = (int)Everything_GetTotResults();
            uint count = Everything_GetNumResults();
            var list = new List<SearchResultItem>((int)count);
            char* buffer = stackalloc char[PathBufferLength];

            for (uint i = 0; i < count; i++)
            {
                bool isFile = Everything_IsFileResult(i);
                uint len = Everything_GetResultFullPathNameW(i, buffer, PathBufferLength);
                if (len == 0)
                    continue;

                var fullPath = new string(buffer, 0, (int)Math.Min(len, PathBufferLength - 1));
                Everything_GetResultSize(i, out long size);
                Everything_GetResultDateModified(i, out FILETIME ft);
                DateTime? modified = FileTimeToDateTime(ft);

                list.Add(CreateResultItem(fullPath, !isFile, size, modified));
            }

            return new EverythingQueryResult(list, totalCount);
        }
        finally
        {
            Everything_CleanUp();
        }
    }

    private uint MapIpcSortType(EverythingSortOrder sortOrder) =>
        sortOrder switch
        {
            EverythingSortOrder.NameAscending => 1,
            EverythingSortOrder.NameDescending => 2,
            EverythingSortOrder.PathAscending => 3,
            EverythingSortOrder.PathDescending => 4,
            EverythingSortOrder.SizeAscending => 5,
            EverythingSortOrder.SizeDescending => 6,
            EverythingSortOrder.ExtensionAscending => 7,
            EverythingSortOrder.DateCreatedAscending => 11,
            EverythingSortOrder.DateCreatedDescending => 12,
            EverythingSortOrder.DateModifiedAscending => 13,
            EverythingSortOrder.DateModifiedDescending => 14,
            EverythingSortOrder.RunCountDescending => 19,
            _ => 1u
        };

    private SearchResultItem CreateResultItem(string fullPath, bool isFolder, long sizeBytes, DateTime? modified)
    {
        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name))
            name = fullPath;

        var directory = Path.GetDirectoryName(fullPath) ?? fullPath;
        var parentFolderName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(parentFolderName))
            parentFolderName = directory;

        var ext = Path.GetExtension(fullPath);
        SearchResultKind kind;
        if (isFolder)
            kind = SearchResultKind.Folder;
        else if (_photoExtensions.Contains(ext))
            kind = SearchResultKind.Photo;
        else if (_appExtensions.Contains(ext))
            kind = SearchResultKind.App;
        else
            kind = SearchResultKind.Document;

        var subtitle = isFolder
            ? $"{parentFolderName} 中的文件夹"
            : $"{parentFolderName} 中的 {name}";

        return new SearchResultItem
        {
            Title = name,
            Subtitle = subtitle,
            FullPath = fullPath,
            DirectoryPath = directory,
            Kind = kind,
            SizeBytes = isFolder ? -1 : sizeBytes,
            DateModified = modified
        };
    }

    private static DateTime? ToDateTime(ulong fileTime)
    {
        if (fileTime == 0 || fileTime == ulong.MaxValue)
            return null;

        if ((long)fileTime <= 0)
            return null;

        return DateTime.FromFileTime((long)fileTime);
    }

    private static DateTime? FileTimeToDateTime(FILETIME ft)
    {
        long combined = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        if (combined <= 0)
            return null;

        return DateTime.FromFileTime(combined);
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            DisconnectPipeLocked();
        }
    }

    [DllImport("Everything3.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr Everything3_ConnectW(string? lpInstanceName);

    [DllImport("Everything3.dll")]
    private static extern bool Everything3_DestroyClient(IntPtr client);

    [DllImport("Everything3.dll")]
    private static extern uint Everything3_GetMajorVersion(IntPtr client);

    [DllImport("Everything3.dll")]
    private static extern uint Everything3_GetMinorVersion(IntPtr client);

    [DllImport("Everything3.dll")]
    private static extern uint Everything3_GetRevision(IntPtr client);

    [DllImport("Everything3.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything3_IncRunCountFromFilenameW(IntPtr client, string lpFilename);

    [DllImport("Everything3.dll")]
    private static extern bool Everything3_IsPropertyFastSort(IntPtr client, uint propertyId);

    [DllImport("Everything3.dll")]
    private static extern IntPtr Everything3_CreateSearchState();

    [DllImport("Everything3.dll")]
    private static extern bool Everything3_DestroySearchState(IntPtr searchState);

    [DllImport("Everything3.dll", CharSet = CharSet.Unicode)]
    private static extern bool Everything3_SetSearchTextW(IntPtr searchState, string lpSearchText);

    [DllImport("Everything3.dll")]
    private static extern bool Everything3_SetSearchMatchCase(IntPtr searchState, bool matchCase);

    [DllImport("Everything3.dll")]
    private static extern bool Everything3_SetSearchMatchPath(IntPtr searchState, bool matchPath);

    [DllImport("Everything3.dll")]
    private static extern bool Everything3_SetSearchMatchWholeWords(IntPtr searchState, bool matchWholeWords);

    [DllImport("Everything3.dll")]
    private static extern bool Everything3_SetSearchRegex(IntPtr searchState, bool matchRegex);

    [DllImport("Everything3.dll")]
    private static extern bool Everything3_AddSearchSort(IntPtr searchState, uint propertyId, bool ascending);

    [DllImport("Everything3.dll")]
    private static extern bool Everything3_AddSearchPropertyRequest(IntPtr searchState, uint propertyId);

    [DllImport("Everything3.dll")]
    private static extern bool Everything3_SetSearchViewportOffset(IntPtr searchState, nuint offset);

    [DllImport("Everything3.dll")]
    private static extern bool Everything3_SetSearchViewportCount(IntPtr searchState, nuint count);

    [DllImport("Everything3.dll")]
    private static extern IntPtr Everything3_Search(IntPtr client, IntPtr searchState);

    [DllImport("Everything3.dll")]
    private static extern bool Everything3_DestroyResultList(IntPtr resultList);

    [DllImport("Everything3.dll")]
    private static extern nuint Everything3_GetResultListCount(IntPtr resultList);

    [DllImport("Everything3.dll")]
    private static extern nuint Everything3_GetResultListViewportCount(IntPtr resultList);

    [DllImport("Everything3.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe nuint Everything3_GetResultFullPathNameW(
        IntPtr resultList,
        nuint resultIndex,
        char* lpString,
        nuint maxCount
    );

    [DllImport("Everything3.dll")]
    private static extern ulong Everything3_GetResultSize(IntPtr resultList, nuint resultIndex);

    [DllImport("Everything3.dll")]
    private static extern ulong Everything3_GetResultDateModified(IntPtr resultList, nuint resultIndex);

    [DllImport("Everything3.dll")]
    private static extern bool Everything3_IsFolderResult(IntPtr resultList, nuint resultIndex);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything_SetSearchW(string lpSearchString);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything_SetInstanceName(string lpInstanceName);

    [DllImport("Everything64.dll")]
    private static extern void Everything_SetMatchPath(bool bEnable);

    [DllImport("Everything64.dll")]
    private static extern void Everything_SetMatchCase(bool bEnable);

    [DllImport("Everything64.dll")]
    private static extern void Everything_SetMatchWholeWord(bool bEnable);

    [DllImport("Everything64.dll")]
    private static extern void Everything_SetRegex(bool bEnable);

    [DllImport("Everything64.dll")]
    private static extern void Everything_SetMax(uint dwMax);

    [DllImport("Everything64.dll")]
    private static extern void Everything_SetOffset(uint dwOffset);

    [DllImport("Everything64.dll")]
    private static extern bool Everything_QueryW(bool bWait);

    [DllImport("Everything64.dll")]
    private static extern uint Everything_GetNumResults();

    [DllImport("Everything64.dll")]
    private static extern uint Everything_GetTotResults();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe uint Everything_GetResultFullPathNameW(
        uint nIndex,
        char* lpString,
        uint nMaxCount
    );

    [DllImport("Everything64.dll")]
    private static extern void Everything_SetSort(uint dwSortType);

    [DllImport("Everything64.dll")]
    private static extern void Everything_SetRequestFlags(uint dwRequestFlags);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything_IncRunCountFromFileName(string lpFileName);

    [DllImport("Everything64.dll")]
    private static extern bool Everything_IsFileResult(uint nIndex);

    [DllImport("Everything64.dll")]
    private static extern uint Everything_GetMajorVersion();

    [DllImport("Everything64.dll")]
    private static extern uint Everything_GetMinorVersion();

    [DllImport("Everything64.dll")]
    private static extern uint Everything_GetRevision();

    [DllImport("Everything64.dll")]
    private static extern bool Everything_IsFastSort(uint sortType);

    [DllImport("Everything64.dll")]
    private static extern bool Everything_GetResultSize(uint nIndex, out long lpFileSize);

    [DllImport("Everything64.dll")]
    private static extern bool Everything_GetResultDateModified(uint nIndex, out FILETIME lpFileTime);

    [DllImport("Everything64.dll")]
    private static extern void Everything_CleanUp();
}
