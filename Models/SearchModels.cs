using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace EverythingonStart.Models;

public enum SearchCategory
{
    All = 0,
    Apps = 1,
    Documents = 2,
    Web = 3,
    Settings = 4,
    Folders = 5,
    Photos = 6
}

public enum SearchResultKind
{
    App,
    Setting,
    Folder,
    Document,
    Photo,
    File,
    WebDirect,
    WebSuggestion
}

public enum WinKeyTriggerMode
{
    DirectOnSingleWinTap = 0,
    InterceptTypingInStartMenu = 1
}

public enum WebSearchEngine
{
    Bing = 0,
    Google = 1,
    Baidu = 2,
    DuckDuckGo = 3
}

public enum EverythingSortOrder
{
    FollowEverything = 0,
    DefaultRelevance = 1,
    NameAscending = 2,
    NameDescending = 3,
    DateModifiedDescending = 4,
    DateModifiedAscending = 5,
    DateCreatedDescending = 6,
    DateCreatedAscending = 7,
    SizeDescending = 8,
    SizeAscending = 9,
    PathAscending = 10,
    PathDescending = 11,
    ExtensionAscending = 12,
    RunCountDescending = 13
}

public enum FilterConfigMode
{
    Default = 0,
    FollowEverything = 1,
    Custom = 2
}

public enum ExtraTypeFilter
{
    None = 0,
    Audio = 1,
    Video = 2,
    Compressed = 3,
    Executable = 4,
    Code = 5
}

public enum InitialSearchItemMode
{
    DefaultAll = 0,
    LastSelectedOnExit = 1
}

public enum AppThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}


public sealed class SearchResultItem : INotifyPropertyChanged
{
    private ImageSource? _smallIcon;
    private ImageSource? _largeIcon;

    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string FullPath { get; init; }
    public string DirectoryPath { get; init; } = string.Empty;
    public string WebSearchQuery { get; init; } = string.Empty;
    public required SearchResultKind Kind { get; init; }
    public long SizeBytes { get; init; } = -1;
    public DateTime? DateModified { get; init; }
    public bool IsFolder => Kind == SearchResultKind.Folder;
    public bool IsWeb => Kind is SearchResultKind.WebDirect or SearchResultKind.WebSuggestion;
    public bool IsSetting => Kind == SearchResultKind.Setting;
    public bool IsApp => Kind == SearchResultKind.App;
    public bool IsFileSystemEntry => Kind is SearchResultKind.Folder or SearchResultKind.Document or SearchResultKind.Photo or SearchResultKind.File or SearchResultKind.App;
    public bool SupportsRunAsAdmin => !IsWeb && !IsSetting && !IsFolder;
    public bool SupportsOpenLocation => !IsWeb && !IsSetting && !string.IsNullOrWhiteSpace(FullPath);
    public bool SupportsCopyPath => !IsWeb && !string.IsNullOrWhiteSpace(FullPath);
    public int MatchScore { get; set; }

    public ImageSource? SmallIcon
    {
        get => _smallIcon;
        set
        {
            if (!ReferenceEquals(_smallIcon, value))
            {
                _smallIcon = value;
                OnPropertyChanged();
            }
        }
    }

    public ImageSource? LargeIcon
    {
        get => _largeIcon;
        set
        {
            if (!ReferenceEquals(_largeIcon, value))
            {
                _largeIcon = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayPreviewIcon));
            }
        }
    }

    public ImageSource? DisplayPreviewIcon => _largeIcon ?? _smallIcon;

    public string FormattedModifiedTime =>
        DateModified.HasValue && DateModified.Value.Year > 1980
            ? DateModified.Value.ToString("yyyy/M/d HH:mm")
            : "实时可用";

    public string FormattedModifiedLine =>
        DateModified.HasValue && DateModified.Value.Year > 1980
            ? $"上次修改时间: {DateModified.Value:yyyy/M/d HH:mm}"
            : string.Empty;

    public bool HasModifiedLine => DateModified.HasValue && DateModified.Value.Year > 1980;

    public string FormattedSizeOrStatus
    {
        get
        {
            if (IsWeb)
                return "联机 Web 搜索";
            if (IsSetting)
                return "Windows 系统设置";
            if (IsFolder)
                return "本地文件夹";
            if (SizeBytes >= 0)
                return FormatSize(SizeBytes);
            return "本地可用";
        }
    }

    public string KindBadgeText =>
        Kind switch
        {
            SearchResultKind.App => "应用",
            SearchResultKind.Setting => "系统设置",
            SearchResultKind.Folder => "文件夹",
            SearchResultKind.Document => "文档",
            SearchResultKind.Photo => "照片",
            SearchResultKind.WebDirect or SearchResultKind.WebSuggestion => "Web 搜索",
            _ => GetExtensionDescription(FullPath)
        };

    public string LocationDisplay =>
        IsWeb ? FullPath
        : IsSetting ? FullPath
        : !string.IsNullOrEmpty(DirectoryPath) ? DirectoryPath
        : FullPath;

    private static string GetExtensionDescription(string path)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrWhiteSpace(ext) ? "文件" : $"{ext.TrimStart('.').ToUpperInvariant()} 文件";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024)
            return $"{kb:F1} KB";
        double mb = kb / 1024.0;
        if (mb < 1024)
            return $"{mb:F1} MB";
        double gb = mb / 1024.0;
        return $"{gb:F2} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class SearchListEntry
{
    public bool IsHeader { get; init; }
    public string HeaderTitle { get; init; } = string.Empty;
    public string HeaderCountBadge { get; init; } = string.Empty;
    public SearchCategory? TargetCategory { get; init; }
    public bool CanExpandCategory => TargetCategory.HasValue && TargetCategory.Value != SearchCategory.All;
    public bool IsBestMatchCard { get; init; }
    public SearchResultItem? Item { get; init; }

    public static SearchListEntry CreateHeader(string title, string countBadge = "", SearchCategory? targetCategory = null) =>
        new()
        {
            IsHeader = true,
            HeaderTitle = title,
            HeaderCountBadge = countBadge,
            TargetCategory = targetCategory
        };

    public static SearchListEntry CreateItem(SearchResultItem item, bool isBestMatch = false) =>
        new()
        {
            IsHeader = false,
            Item = item,
            IsBestMatchCard = isBestMatch
        };
}
