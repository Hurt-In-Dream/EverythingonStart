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
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

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
            item.SmallIcon = cachedSmall;
        else
            item.SmallIcon = item.IsFolder ? _defaultFolderIcon : _defaultFileIcon;

        if (_largeIconCache.TryGetValue(cacheKey, out var cachedLarge))
        {
            item.LargeIcon = cachedLarge;
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            var small = _smallIconCache.GetOrAdd(cacheKey, _ => LoadIconForItem(item, large: false));
            var large = _largeIconCache.GetOrAdd(cacheKey, _ => LoadIconForItem(item, large: true));

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                item.SmallIcon = small;
                item.LargeIcon = large;
            });
        });
    }

    private static string GetCacheKey(SearchResultItem item)
    {
        if (item.IsFolder)
            return "__FOLDER__";

        var ext = Path.GetExtension(item.FullPath);
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase))
        {
            return item.FullPath;
        }

        return string.IsNullOrEmpty(ext) ? item.FullPath : $"__EXT__{ext.ToLowerInvariant()}";
    }

    private ImageSource LoadIconForItem(SearchResultItem item, bool large)
    {
        var ext = Path.GetExtension(item.FullPath);
        bool perFileIcon = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".msc", StringComparison.OrdinalIgnoreCase);

        bool useAttributesOnly = !perFileIcon && (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath));
        var target = useAttributesOnly && !item.IsFolder && !string.IsNullOrEmpty(ext)
            ? ext
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
        var bgBrush = new SolidColorBrush(Color.FromArgb(40, accentColor.R, accentColor.G, accentColor.B));
        var pen = new Pen(new SolidColorBrush(accentColor), 2.0)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        group.Children.Add(new GeometryDrawing(bgBrush, null, new EllipseGeometry(new Point(16, 16), 15, 15)));
        group.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new Point(16, 16), 6.5, 6.5)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(accentColor), null, new EllipseGeometry(new Point(16, 16), 2.2, 2.2)));

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
