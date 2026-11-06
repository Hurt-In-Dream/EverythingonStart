using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EverythingonStart.Services;

public static class AppIconService
{
    private const string ResourceUri = "pack://application:,,,/EverythingonStart;component/Assets/";
    public static ImageSource WindowIcon { get; } = LoadImage();

    private static ImageSource LoadImage()
    {
        var image = new BitmapImage(new Uri(ResourceUri + "AppIcon.png"));
        image.Freeze();
        return image;
    }

    public static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = Application.GetResourceStream(new Uri(ResourceUri + "AppIcon.ico"))!.Stream;
        using var icon = new System.Drawing.Icon(stream, 32, 32);
        return (System.Drawing.Icon)icon.Clone();
    }
}
