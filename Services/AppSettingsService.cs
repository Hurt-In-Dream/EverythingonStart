using System.IO;
using System.Text.Json;
using EverythingonStart.Models;
using Microsoft.Win32;

namespace EverythingonStart.Services;

public sealed class AppSettings
{
    public const string DefaultAppsExtensions = "exe;msi;lnk;bat;cmd;appref-ms;ps1;msc;scr";
    public const string DefaultDocumentsExtensions =
        "doc;docx;docm;dot;dotx;pdf;txt;md;markdown;rtf;xls;xlsx;xlsm;xlsb;csv;tsv;ppt;pptx;pptm;pps;ppsx;"
        + "odt;ods;odp;wps;et;dps;epub;mobi;azw3;chm;vsd;vsdx;xmind;htm;html;mht;mhtml;xml;json;yaml;yml;toml;ini;cfg;conf;log;sql;"
        + "c;cpp;cxx;cc;h;hpp;cs;java;py;js;ts;tsx;jsx;vue;go;rs;php;rb;swift;kt;lua;r;sh;ps1";
    public const string DefaultFoldersFilter = "folder:";
    public const string DefaultPhotosExtensions =
        "jpg;jpeg;jpe;png;gif;webp;bmp;dib;svg;ico;cur;tif;tiff;heic;heif;avif;raw;arw;cr2;nef;dng;psd;psb;ai;eps;pcx;tga;wmf;emf;dds;hdr;exr";
    public const string DefaultAudioExtensions =
        "mp3;flac;wav;m4a;aac;ogg;oga;opus;wma;ape;alac;aif;aiff;cda;mid;midi;mka;amr;dsf;dff";
    public const string DefaultVideoExtensions =
        "mp4;mkv;avi;mov;wmv;flv;f4v;webm;m4v;mpg;mpeg;m2ts;mts;ts;vob;3gp;3g2;rm;rmvb;asf;ogv;divx";
    public const string DefaultCompressedExtensions =
        "zip;rar;7z;tar;gz;tgz;bz2;tbz2;xz;txz;zst;lz4;iso;img;cab;arj;lzh;ace;jar;war;apk;ipa;nupkg;vsix";
    public const string DefaultCodeExtensions =
        "cs;c;cpp;h;hpp;py;js;ts;tsx;jsx;java;go;rs;php;rb;swift;kt;sql;sh;ps1;bat;cmd;json;xml;yaml;yml;toml;html;css;scss;less;vue;svelte;lua;dart;scala";

    public const string EverythingNativeExecutableExtensions = "bat;cmd;exe;msi;msp;scr";
    public const string EverythingNativeDocumentExtensions =
        "c;chm;cpp;csv;cxx;doc;docm;docx;dot;dotm;dotx;h;hpp;htm;html;hxx;ini;java;lua;mht;mhtml;odt;pdf;potx;potm;ppam;ppsm;ppsx;pps;ppt;pptm;pptx;rtf;sldm;sldx;thmx;txt;vsd;wpd;wps;wri;xlam;xls;xlsb;xlsm;xlsx;xltm;xltx;xml";
    public const string EverythingNativePictureExtensions =
        "ani;bmp;gif;ico;jpe;jpeg;jpg;pcx;png;psd;tga;tif;tiff;webp;wmf";
    public const string EverythingNativeAudioExtensions =
        "aac;ac3;aif;aifc;aiff;au;cda;dts;fla;flac;it;m1a;m2a;m3u;m4a;mid;midi;mka;mod;mp2;mp3;mpa;ogg;ra;rmi;spc;snd;umx;voc;wav;wma;xm";
    public const string EverythingNativeVideoExtensions =
        "3g2;3gp;3gp2;3gpp;amr;amv;asf;avi;bdmv;bik;d2v;divx;drc;dsa;dsm;dss;dsv;evo;f4v;flc;fli;flic;flv;hdmov;ifo;ivf;m1v;m2p;m2t;m2ts;m2v;m4b;m4p;m4v;mkv;mp2v;mp4;mp4v;mpe;mpeg;mpg;mpls;mpv2;mpv4;mov;mts;ogm;ogv;pss;pva;qt;ram;ratdvd;rm;rmm;rmvb;roq;rpm;smil;smk;swf;tp;tpr;ts;vob;vp6;webm;wm;wmp;wmv";
    public const string EverythingNativeCompressedExtensions =
        "7z;ace;arj;bz2;cab;gz;gzip;jar;r00;r01;r02;r03;r04;r05;r06;r07;r08;r09;r10;r11;r12;r13;r14;r15;r16;r17;r18;r19;r20;r21;r22;r23;r24;r25;r26;r27;r28;r29;rar;tar;tgz;z;zip";

    public int Version { get; set; } = 3;
    public WinKeyTriggerMode TriggerMode { get; set; } = WinKeyTriggerMode.InterceptTypingInStartMenu;
    public WebSearchEngine SearchEngine { get; set; } = WebSearchEngine.Bing;
    public string EverythingInstanceName { get; set; } = string.Empty;
    public string CustomEverythingExecutablePath { get; set; } = string.Empty;
    public string CustomEverythingFiltersCsvPath { get; set; } = string.Empty;
    public string CustomEverythingIniPath { get; set; } = string.Empty;

    // 排序与搜索修饰开关
    public EverythingSortOrder SortOrder { get; set; } = EverythingSortOrder.DefaultRelevance;
    public ExtraTypeFilter ActiveExtraFilter { get; set; } = ExtraTypeFilter.None;
    public bool FollowEverythingSearchFlags { get; set; }
    public bool MatchCase { get; set; }
    public bool MatchWholeWord { get; set; }
    public bool MatchPath { get; set; }
    public bool EnableRegex { get; set; }

    // 分类子选项文件类型筛选模式（默认 / 跟随 Everything 设置 / 自定义）
    public FilterConfigMode FilterMode { get; set; } = FilterConfigMode.Default;
    public bool UsePerCategoryFilterMode { get; set; }
    public FilterConfigMode AppsFilterMode { get; set; } = FilterConfigMode.Default;
    public FilterConfigMode DocumentsFilterMode { get; set; } = FilterConfigMode.Default;
    public FilterConfigMode FoldersFilterMode { get; set; } = FilterConfigMode.Default;
    public FilterConfigMode PhotosFilterMode { get; set; } = FilterConfigMode.Default;

    // 自定义文件类型（扩展名列表或 Everything 筛选表达式）
    public string CustomAppsFilter { get; set; } = DefaultAppsExtensions;
    public string CustomDocumentsFilter { get; set; } = DefaultDocumentsExtensions;
    public bool DefaultDocumentsMatchAllFiles { get; set; } = true;
    public string CustomFoldersFilter { get; set; } = DefaultFoldersFilter;
    public string CustomPhotosFilter { get; set; } = DefaultPhotosExtensions;
    public string CustomAudioFilter { get; set; } = DefaultAudioExtensions;
    public string CustomVideoFilter { get; set; } = DefaultVideoExtensions;
    public string CustomCompressedFilter { get; set; } = DefaultCompressedExtensions;
    public string CustomCodeFilter { get; set; } = DefaultCodeExtensions;

    // 搜索结果数量与数据源开关
    public int MaxResultsPerGroupInAll { get; set; } = 6;
    public int MaxResultsInSingleCategory { get; set; } = 120;
    public bool IncludeStartMenuApps { get; set; } = true;
    public bool IncludeEverythingApps { get; set; } = true;
    public bool ShowWebSuggestionsInAll { get; set; } = true;
    public bool ShowSettingsInAll { get; set; } = true;

    // 呼出与窗口交互设置
    public InitialSearchItemMode InitialSearchItem { get; set; } = InitialSearchItemMode.DefaultAll;
    public SearchCategory LastSearchCategory { get; set; } = SearchCategory.All;
    public bool BackButtonClosesOnAllTab { get; set; } = true;
    public bool KeepTaskbarVisibleInFullscreen { get; set; } = true;
    public bool EscClearsSearchFirst { get; set; } = true;
    public bool AutoHideOnDeactivate { get; set; } = true;
    public bool ClearSearchOnOpen { get; set; }
    public bool ResetToAllCategoryOnOpen
    {
        get => InitialSearchItem == InitialSearchItemMode.DefaultAll;
        set => InitialSearchItem = value
            ? InitialSearchItemMode.DefaultAll
            : InitialSearchItemMode.LastSelectedOnExit;
    }

    // 尺寸缩放与视觉效果
    public bool AutoScaleToStartMenu { get; set; } = true;
    public double ManualUiScale { get; set; } = 1.0;
    public double WindowOpacityPercent { get; set; } = 95.0;
    public bool EnablePopupAnimation { get; set; } = true;

    public double CachedStartMenuCardWidthDip { get; set; }
    public double CachedStartMenuCardHeightDip { get; set; }
    public double CachedStartMenuBottomGapDip { get; set; } = 10.0;
    public double CachedScreenWidthDip { get; set; }
    public double CachedScreenHeightDip { get; set; }

    public FilterConfigMode GetEffectiveCategoryMode(SearchCategory category)
    {
        if (!UsePerCategoryFilterMode)
            return FilterMode;

        return category switch
        {
            SearchCategory.Apps => AppsFilterMode,
            SearchCategory.Documents => DocumentsFilterMode,
            SearchCategory.Folders => FoldersFilterMode,
            SearchCategory.Photos => PhotosFilterMode,
            _ => FilterMode
        };
    }
}

public sealed class AppSettingsService
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRunValueName = "EverythingonStart";

    private readonly string _settingsFilePath;

    public AppSettings Current { get; private set; } = new();

    public AppSettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EverythingonStart"
        );
        Directory.CreateDirectory(dir);
        _settingsFilePath = Path.Combine(dir, "settings.json");
        Load();
    }

    public void Load()
    {
        if (!File.Exists(_settingsFilePath))
            return;

        var json = File.ReadAllText(_settingsFilePath);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);
        if (loaded != null)
        {
            if (loaded.Version < 2)
            {
                loaded.Version = 2;
                loaded.TriggerMode = WinKeyTriggerMode.InterceptTypingInStartMenu;
                Current = loaded;
                Save();
                return;
            }

            Current = loaded;
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsFilePath, json);
    }

    public bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: false);
        return key?.GetValue(AppRunValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
        if (key == null)
            return;

        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
                key.SetValue(AppRunValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppRunValueName, throwOnMissingValue: false);
        }
    }

    public void ResetCategoryFiltersToDefault()
    {
        Current.CustomAppsFilter = AppSettings.DefaultAppsExtensions;
        Current.CustomDocumentsFilter = AppSettings.DefaultDocumentsExtensions;
        Current.DefaultDocumentsMatchAllFiles = true;
        Current.CustomFoldersFilter = AppSettings.DefaultFoldersFilter;
        Current.CustomPhotosFilter = AppSettings.DefaultPhotosExtensions;
        Current.CustomAudioFilter = AppSettings.DefaultAudioExtensions;
        Current.CustomVideoFilter = AppSettings.DefaultVideoExtensions;
        Current.CustomCompressedFilter = AppSettings.DefaultCompressedExtensions;
        Current.CustomCodeFilter = AppSettings.DefaultCodeExtensions;
        Save();
    }

    public string BuildWebSearchUrl(string query)
    {
        var encoded = Uri.EscapeDataString(query.Trim());
        return Current.SearchEngine switch
        {
            WebSearchEngine.Google => $"https://www.google.com/search?q={encoded}",
            WebSearchEngine.Baidu => $"https://www.baidu.com/s?wd={encoded}",
            WebSearchEngine.DuckDuckGo => $"https://duckduckgo.com/?q={encoded}",
            _ => $"https://www.bing.com/search?q={encoded}"
        };
    }
}
