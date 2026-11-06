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

    private readonly ImageSource _webIcon;
    private readonly ImageSource _settingIcon;
    private readonly ImageSource _defaultFolderIcon;
    private readonly ImageSource _defaultFileIcon;

    public IconCacheService()
    {
        _webIcon = CreateVectorSearchBadgeIcon(Color.FromRgb(56, 189, 248));
        _settingIcon = CreateVectorGearBadgeIcon(Color.FromRgb(20, 184, 166));
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

    private static DrawingImage CreateVectorSearchBadgeIcon(Color accentColor)
    {
        var group = new DrawingGroup();
        var bgBrush = new SolidColorBrush(Color.FromArgb(40, accentColor.R, accentColor.G, accentColor.B));
        var pen = new Pen(new SolidColorBrush(accentColor), 2.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        group.Children.Add(new GeometryDrawing(bgBrush, null, new EllipseGeometry(new Point(16, 16), 15, 15)));
        group.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new Point(14.5, 14.5), 5.5, 5.5)));
        group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(18.5, 18.5), new Point(23.0, 23.0))));

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
