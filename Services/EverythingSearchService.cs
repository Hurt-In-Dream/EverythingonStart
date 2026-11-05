using System.IO;
using System.Runtime.InteropServices;
using EverythingonStart.Models;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace EverythingonStart.Services;

public readonly record struct EverythingQueryResult(IReadOnlyList<SearchResultItem> Items, int TotalCount);

public sealed class EverythingSearchService : IDisposable
{
    private const int PathBufferLength = 4096;
    private const uint PropertyIdName = 0;
    private const uint PropertyIdPath = 1;
    private const uint PropertyIdSize = 2;
    private const uint PropertyIdDateModified = 5;
    private const uint PropertyIdRunCount = 10;
    private const uint PropertyIdPathAndName = 240;

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".ico", ".tif", ".tiff", ".heic", ".raw", ".psd"
    };

    private static readonly HashSet<string> AppExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".lnk", ".bat", ".cmd", ".appref-ms"
    };

    private readonly object _syncLock = new();
    private IntPtr _pipeClient;
    private bool _usePipeSdk;
    private bool _useIpcSdk;
    private bool _pipeFastRunCountSort;
    private bool _ipcFastRunCountSort;
    private string _activeEngineDescription = "未连接";
    private string _configuredInstanceName = string.Empty;

    public string ActiveEngineDescription => _activeEngineDescription;

    public bool IsConnected => _usePipeSdk || _useIpcSdk;

    public EverythingSearchService(string instanceName = "")
    {
        _configuredInstanceName = instanceName;
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

    private static string BuildCategoryQuery(string searchText, SearchCategory category) =>
        category switch
        {
            SearchCategory.Folders => $"folder: {searchText}",
            SearchCategory.Documents => $"!folder: {searchText}",
            SearchCategory.Photos => $"!folder: ext:jpg;jpeg;png;gif;webp;bmp;svg;ico;tif;tiff;heic;raw;psd {searchText}",
            SearchCategory.Apps => $"!folder: ext:exe;msi;lnk;bat;cmd;appref-ms {searchText}",
            _ => searchText
        };

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
            Everything3_SetSearchTextW(searchState, queryText);
            Everything3_SetSearchMatchCase(searchState, false);
            Everything3_SetSearchMatchPath(searchState, false);
            Everything3_SetSearchMatchWholeWords(searchState, false);
            Everything3_SetSearchRegex(searchState, false);

            if (_pipeFastRunCountSort)
                Everything3_AddSearchSort(searchState, PropertyIdRunCount, ascending: false);
            Everything3_AddSearchSort(searchState, PropertyIdName, ascending: true);

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

            int totalCount = (int)Math.Min( Everything3_GetResultListCount(resultList), (nuint)int.MaxValue);
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

    private unsafe EverythingQueryResult QueryViaIpcLocked(string queryText, int maxResults)
    {
        const uint RequestFullPathAndFileName = 0x00000004;
        const uint RequestSize = 0x00000010;
        const uint RequestDateModified = 0x00000040;
        const uint SortRunCountDescending = 19;
        const uint SortNameAscending = 1;

        Everything_SetSearchW(queryText);
        Everything_SetRequestFlags(RequestFullPathAndFileName | RequestSize | RequestDateModified);
        Everything_SetMatchCase(false);
        Everything_SetMatchPath(false);
        Everything_SetMatchWholeWord(false);
        Everything_SetRegex(false);
        Everything_SetSort(_ipcFastRunCountSort ? SortRunCountDescending : SortNameAscending);
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

    private static SearchResultItem CreateResultItem(string fullPath, bool isFolder, long sizeBytes, DateTime? modified)
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
        else if (PhotoExtensions.Contains(ext))
            kind = SearchResultKind.Photo;
        else if (AppExtensions.Contains(ext))
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
