using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EverythingonStart.Models;

namespace EverythingonStart.Services;

public sealed class IconCacheService
{
    // MingCute Icons (Apache-2.0), SVG viewBox 0 0 24 24; original assets are in Assets/Icons.
    public const string MingCuteSearchLinePathData = "F1 m17.176 15.762l3.652 3.652c.359.41.405 1.01 0 1.414c-.404.405-1.004.359-1.414 0l-3.652-3.652c-1.145.85-2.399 1.459-3.814 1.702C5.871 19.921.72 14.327 2.272 8.355a8.5 8.5 0 0 1 1.979-3.618A8.55 8.55 0 0 1 7.68 2.478c6.112-2.146 12.292 3.095 11.198 9.47c-.243 1.415-.852 2.669-1.702 3.814M9.938 4.025a6.5 6.5 0 0 0-3.844 1.697a6.5 6.5 0 0 0-1.727 2.622c-1.558 4.436 2.021 9.03 6.695 8.631a6.5 6.5 0 0 0 3.844-1.697a6.5 6.5 0 0 0 1.727-2.622c1.558-4.436-2.021-9.03-6.695-8.631";
    public const string MingCuteEarth2LinePathData = "F1 M12.038 21.999L12 21l-.038.999c-1.549-.059-2.958-.328-4.36-1.015a10.1 10.1 0 0 1-4.586-4.585C.469 11.203 2.915 4.865 8.279 2.715c1.19-.477 2.409-.665 3.683-.714L12 3l.038-.999c1.274.049 2.493.237 3.683.714c5.364 2.15 7.81 8.488 5.263 13.684a10.1 10.1 0 0 1-4.585 4.585c-1.403.687-2.812.956-4.361 1.015m-1.522-5.066c.225.897.474 1.855 1.016 2.623c.398.564.538.564.936 0c.225-.319.392-.68.535-1.042c.204-.514.347-1.045.481-1.581c-.985.093-1.983.093-2.968 0m8.681-1.438c-1.173.505-2.363.863-3.608 1.142c-.187.981-.44 1.952-.836 2.871c1.995-.715 3.474-2.156 4.444-4.013M8.411 16.637c-1.245-.279-2.435-.637-3.608-1.142c.97 1.857 2.449 3.298 4.444 4.013c-.396-.919-.649-1.89-.836-2.871m1.616-5.768a26.4 26.4 0 0 0 .14 4.021c1.249.119 2.417.119 3.666 0c.145-1.338.2-2.676.14-4.021c-.873.09-1.065.132-1.973.132s-1.1-.042-1.973-.132m-1.984-.4c-1.223-.32-2.371-.828-3.471-1.443c-.456 1.273-.675 2.502-.517 3.857a15 15 0 0 0 4.06 1.638a31.5 31.5 0 0 1-.072-4.052m11.385-1.443c-1.1.615-2.248 1.123-3.471 1.443c.061 1.356.041 2.7-.072 4.052a15 15 0 0 0 4.06-1.638c.158-1.355-.061-2.584-.517-3.857m-9.233-.151c.8.083.996.124 1.805.124s1.005-.041 1.805-.124c-.198-1.401-.446-2.902-1.16-4.149c-.125-.219-.278-.469-.482-.627c-.211-.163-.285-.064-.466.136l-.165.209c-.225.319-.392.68-.535 1.042l-.329.987c-.222.788-.358 1.592-.473 2.402M5.547 7.28c.864.483 1.747.86 2.69 1.161c.19-1.348.47-2.694 1.01-3.949c-1.489.534-2.759 1.524-3.7 2.788m9.206-2.788c.54 1.254.82 2.601 1.01 3.949a14.5 14.5 0 0 0 2.69-1.161c-.941-1.264-2.211-2.254-3.7-2.788";
    public const string MingCuteExternalLinkLinePathData = "F1 M5.006 8.006v10.988h10.988l.006-.051V13c.042-.639.603-1.142 1.259-.966c.453.122.711.515.741.966v6c-.029.799-.474 1.524-1.224 1.84c-.247.104-.509.15-.776.16H5c-.569-.021-1.103-.239-1.487-.667C3.18 19.961 3.018 19.494 3 19V8c.01-.267.056-.529.16-.776C3.476 6.474 4.201 6.029 5 6h6c.451.03.844.288.966.741c.176.656-.327 1.217-.966 1.259H5.057ZM17.586 5H15c-1.204-.079-1.357-1.672-.259-1.966L15 3h5c.271.018.511.096.707.293c.197.196.275.436.293.707v5l-.034.259c-.294 1.098-1.887.945-1.966-.259V6.414l-8.293 8.293c-.409.359-1.01.404-1.414 0s-.359-1.005 0-1.414Z";
    public static Geometry MingCuteSearchLineGeometry { get; } = FrozenGeometry(MingCuteSearchLinePathData);
    public static Geometry MingCuteEarth2LineGeometry { get; } = FrozenGeometry(MingCuteEarth2LinePathData);
    public static Geometry MingCuteExternalLinkLineGeometry { get; } = FrozenGeometry(MingCuteExternalLinkLinePathData);

    private static Geometry FrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    public const string MingCuteSettings4LinePathData =
        "F0 M13.414,2.1 A2,2 0 0 0 10.586,2.1 L8.686,4 H6 A2,2 0 0 0 4,6 V8.686 L2.1,10.586 A2,2 0 0 0 2.1,13.414 L4,15.314 V18 A2,2 0 0 0 6,20 H8.686 L10.586,21.9 A2,2 0 0 0 13.414,21.9 L15.314,20 H18 A2,2 0 0 0 20,18 V15.314 L21.9,13.414 A2,2 0 0 0 21.9,10.586 L20,8.686 V6 A2,2 0 0 0 18,4 H15.314 L13.414,2.1 Z "
        + "M12,3.515 L14.182,5.697 A1,1 0 0 0 14.889,5.99 H18 V9.111 A1,1 0 0 0 18.293,9.818 L20.485,12 L18.293,14.182 A1,1 0 0 0 18,14.889 V18 H14.889 A1,1 0 0 0 14.182,18.293 L12,20.485 L9.818,18.293 A1,1 0 0 0 9.111,18 H6 V14.889 A1,1 0 0 0 5.707,14.182 L3.515,12 L5.707,9.818 A1,1 0 0 0 6,9.111 V6 H9.111 A1,1 0 0 0 9.818,5.697 L12,3.515 Z "
        + "M12,8.5 A3.5,3.5 0 1 0 12,15.5 A3.5,3.5 0 1 0 12,8.5 Z "
        + "M12,10.5 A1.5,1.5 0 1 1 12,13.5 A1.5,1.5 0 1 1 12,10.5 Z";

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const int MaxSmallCacheEntries = 256;
    private const int MaxLargeCacheEntries = 64;

    private readonly ConcurrentDictionary<string, ImageSource> _smallIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ImageSource> _largeIconCache = new(StringComparer.OrdinalIgnoreCase);

    private ImageSource _webIcon = null!;
    private ImageSource _settingIcon = null!;
    private readonly ImageSource _defaultFolderIcon;
    private readonly ImageSource _defaultFileIcon;

    public IconCacheService()
    {
        RefreshThemeIcons();
        _defaultFolderIcon = ExtractShellIcon("folder", isFolder: true, large: true, useAttributesOnly: true) ?? _webIcon;
        _defaultFileIcon = ExtractShellIcon(".txt", isFolder: false, large: true, useAttributesOnly: true) ?? _webIcon;
    }

    public void PopulateIconsAsync(SearchResultItem item)
    {
        if (item.IsWeb)
        {
            item.SmallIcon = _webIcon;
            item.LargeIcon = _webIcon;
            return;
        }

        if (item.IsSetting)
        {
            item.SmallIcon = _settingIcon;
            item.LargeIcon = _settingIcon;
            return;
        }

        var cacheKey = GetCacheKey(item);
        if (_smallIconCache.TryGetValue(cacheKey, out var cachedSmall))
        {
            item.SmallIcon = cachedSmall;
            item.LargeIcon ??= _largeIconCache.TryGetValue(cacheKey, out var cachedLarge) ? cachedLarge : cachedSmall;
            return;
        }

        var fallback = item.IsFolder ? _defaultFolderIcon : _defaultFileIcon;
        item.SmallIcon = fallback;
        item.LargeIcon ??= fallback;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            TrimCacheIfNeeded(_smallIconCache, MaxSmallCacheEntries);
            var small = _smallIconCache.GetOrAdd(cacheKey, _ => LoadIconForItem(item, large: false));

            Application.Current?.Dispatcher.BeginInvoke(
                () =>
                {
                    item.SmallIcon = small;
                    if (ReferenceEquals(item.LargeIcon, fallback))
                        item.LargeIcon = small;
                },
                System.Windows.Threading.DispatcherPriority.Background
            );
        });
    }

    public void RefreshThemeIcons()
    {
        var accent = Application.Current?.TryFindResource(ThemeResourceKeys.AccentTextBrush) as SolidColorBrush;
        var color = accent?.Color ?? Color.FromRgb(88, 182, 234);
        _webIcon = CreateMingCuteSearchBadgeIcon(color);
        _settingIcon = CreateVectorGearBadgeIcon(color);
    }

    public void PopulateLargeIconAsync(SearchResultItem item)
    {
        if (item.IsWeb || item.IsSetting)
            return;

        var cacheKey = GetCacheKey(item);
        if (_largeIconCache.TryGetValue(cacheKey, out var cachedLarge))
        {
            item.LargeIcon = cachedLarge;
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            TrimCacheIfNeeded(_largeIconCache, MaxLargeCacheEntries);
            var large = _largeIconCache.GetOrAdd(cacheKey, _ => LoadIconForItem(item, large: true));

            Application.Current?.Dispatcher.BeginInvoke(
                () => item.LargeIcon = large,
                System.Windows.Threading.DispatcherPriority.Background
            );
        });
    }

    private static void TrimCacheIfNeeded(ConcurrentDictionary<string, ImageSource> cache, int maxEntries)
    {
        if (cache.Count <= maxEntries)
            return;

        int toRemove = cache.Count - (maxEntries * 3 / 4);
        foreach (var key in cache.Keys)
        {
            if (toRemove-- <= 0)
                break;
            cache.TryRemove(key, out _);
        }
    }

    private static string GetCacheKey(SearchResultItem item)
    {
        if (item.IsFolder)
            return "__FOLDER__";

        var ext = Path.GetExtension(item.FullPath);
        if (IsPerFileIconExtension(ext))
            return item.FullPath;

        return string.IsNullOrEmpty(ext) ? "__NO_EXT__" : $"__EXT__{ext.ToLowerInvariant()}";
    }

    private static bool IsPerFileIconExtension(string ext) =>
        ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".msc", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".url", StringComparison.OrdinalIgnoreCase);

    private ImageSource LoadIconForItem(SearchResultItem item, bool large)
    {
        var ext = Path.GetExtension(item.FullPath);
        bool perFileIcon = !item.IsFolder && IsPerFileIconExtension(ext);
        bool useAttributesOnly = !perFileIcon;
        var target = useAttributesOnly
            ? (item.IsFolder ? "folder" : (string.IsNullOrEmpty(ext) ? ".file" : ext))
            : item.FullPath;

        var extracted = ExtractShellIcon(target, item.IsFolder, large, useAttributesOnly);
        if (extracted != null)
            return extracted;

        return item.IsFolder ? _defaultFolderIcon : _defaultFileIcon;
    }

    private static ImageSource? ExtractShellIcon(string pathOrExt, bool isFolder, bool large, bool useAttributesOnly)
    {
        uint flags = SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);
        uint attributes = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

        if (useAttributesOnly)
            flags |= SHGFI_USEFILEATTRIBUTES;

        var shinfo = new SHFILEINFOW();
        nint res = SHGetFileInfoW(
            pathOrExt,
            attributes,
            ref shinfo,
            (uint)Marshal.SizeOf<SHFILEINFOW>(),
            flags
        );

        if (res == 0 || shinfo.hIcon == IntPtr.Zero)
            return null;

        var bitmap = Imaging.CreateBitmapSourceFromHIcon(
            shinfo.hIcon,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions()
        );
        bitmap.Freeze();
        DestroyIcon(shinfo.hIcon);
        return bitmap;
    }

    private static DrawingImage CreateMingCuteSearchBadgeIcon(Color accentColor)
    {
        var group = new DrawingGroup();
        var bgBrush = new SolidColorBrush(Color.FromArgb(40, accentColor.R, accentColor.G, accentColor.B));
        group.Children.Add(new GeometryDrawing(bgBrush, null, new EllipseGeometry(new Point(12, 12), 11.5, 11.5)));
        var geometry = MingCuteSearchLineGeometry.Clone();
        geometry.Transform = new ScaleTransform(0.78, 0.78, 12, 12);
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(accentColor), null, geometry));
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static DrawingImage CreateVectorGearBadgeIcon(Color accentColor)
    {
        var group = new DrawingGroup();
        var bgBrush = new SolidColorBrush(Color.FromArgb(36, accentColor.R, accentColor.G, accentColor.B));
        bgBrush.Freeze();
        var fgBrush = new SolidColorBrush(accentColor);
        fgBrush.Freeze();

        // 24x24 坐标系：外层柔和圆角底衬 + mingcute:settings-4-line 矢量图标
        group.Children.Add(new GeometryDrawing(bgBrush, null, new EllipseGeometry(new Point(12, 12), 11.5, 11.5)));

        var iconGeometry = Geometry.Parse(MingCuteSettings4LinePathData).Clone();
        iconGeometry.Transform = new ScaleTransform(0.76, 0.76, 12.0, 12.0);
        iconGeometry.Freeze();
        group.Children.Add(new GeometryDrawing(fgBrush, null, iconGeometry));

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFOW psfi,
        uint cbFileInfo,
        uint uFlags
    );

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
