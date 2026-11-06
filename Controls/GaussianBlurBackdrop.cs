using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace EverythingonStart.Controls;

// An input-transparent background layer. Never apply BlurEffect to the window/content tree.
public sealed class GaussianBlurBackdrop : Border
{
    private readonly Image _image;
    private readonly Border _tint;
    private readonly BlurEffect _blur = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };

    public static readonly DependencyProperty TintBrushProperty = DependencyProperty.Register(
        nameof(TintBrush), typeof(Brush), typeof(GaussianBlurBackdrop),
        new PropertyMetadata(Brushes.Transparent, (d, e) => ((GaussianBlurBackdrop)d)._tint.Background = (Brush)e.NewValue));

    public Brush TintBrush
    {
        get => (Brush)GetValue(TintBrushProperty);
        set => SetValue(TintBrushProperty, value);
    }

    public double BlurRadius { get => _blur.Radius; set => _blur.Radius = Math.Clamp(value, 6, 40); }
    public double TintOpacity { get => _tint.Opacity; set => _tint.Opacity = Math.Clamp(value, 0, 1); }
    public BitmapSource? Snapshot => _image.Source as BitmapSource;

    public GaussianBlurBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Visibility = Visibility.Collapsed;
        ClipToBounds = true;
        _image = new Image { Stretch = Stretch.Fill, Effect = _blur };
        _tint = new Border { Background = TintBrush };
        var layers = new Grid();
        layers.Children.Add(_image);
        layers.Children.Add(_tint);
        Child = layers;
    }

    public void SetSnapshot(BitmapSource snapshot)
    {
        if (!snapshot.IsFrozen)
        {
            snapshot = snapshot.Clone();
            snapshot.Freeze();
        }
        _image.Source = snapshot;
        CacheMode = new BitmapCache { SnapsToDevicePixels = true };
        Visibility = Visibility.Visible;
    }

    public void ClearSnapshot()
    {
        _image.Source = null;
        CacheMode = null;
        Visibility = Visibility.Collapsed;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        base.MeasureOverride(constraint);
        // Bitmap pixel dimensions must not increase window/row minimum sizes, especially at high DPI.
        return new Size();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var clip = new RectangleGeometry(new Rect(RenderSize), CornerRadius.TopLeft, CornerRadius.TopLeft);
        clip.Freeze();
        Clip = clip;
    }
}
