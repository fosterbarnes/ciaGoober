using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CiaGoober.Helpers;
using SkiaSharp;

namespace CiaGoober.Windows;

public enum CropTarget { Banner, Icon }

public sealed record CropResult(CropTarget Target, CropRect Crop);

public partial class CropWindow : Window
{
    private const double _FRAME_HEIGHT = 256, _KEY_STEP = 16, _KEY_ZOOM = 1.25, _WHEEL_BASE = 1.0015;
    // Longest side of the on-screen copy; the crop math stays in full source pixels.
    private const int _DISPLAY_LIMIT = 4096;
    private static readonly TimeSpan _PREVIEW_DELAY = TimeSpan.FromMilliseconds(120);

    private readonly string _source, _iconBackground;
    private readonly CropTarget _initialTarget;
    private readonly CropRect? _initialCrop;
    private readonly Brush _iconFill;
    private readonly DispatcherTimer _previewTimer = new() { Interval = _PREVIEW_DELAY };
    private CropTarget _target;
    private LoadedImage? _image;
    private CropView _view;
    private Point? _dragFrom;
    private bool _syncingSlider;
    private int _previewVersion;
    private CropResult? _result;

    // Modal crop of source (CropRect in source pixels, display orientation; it may reach past the
    // image edges). crop = the card's current crop, null = initial framing. allowTargetSwitch lets
    // the LaunchBox browser switch banner/icon before applying. Null when cancelled.
    public static CropResult? Pick(Window owner, string source, CropTarget target, CropRect? crop,
        string iconBackground, bool allowTargetSwitch)
    {
        if (crop is { } area && !(area.Width > 0 && area.Height > 0
            && double.IsFinite(area.Left + area.Top + area.Width + area.Height)))
            throw new ArgumentException($"Invalid crop area: {area}", nameof(crop));
        var window = new CropWindow(source, target, crop, IconColor.Normalize(iconBackground),
            allowTargetSwitch) { Owner = owner };
        return window.ShowDialog() == true ? window._result : null;
    }

    private CropWindow(string source, CropTarget target, CropRect? crop, string iconBackground,
        bool allowTargetSwitch)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowsTitleBarTheme.ApplyImmersiveDarkMode(this);
        _source = source;
        _initialTarget = target;
        _initialCrop = crop;
        _iconBackground = iconBackground;
        var fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(iconBackground));
        fill.Freeze();
        _iconFill = fill;
        _previewTimer.Tick += (_, _) => RenderPreview();
        SetTarget(target);
        TargetPanel.Visibility = allowTargetSwitch ? Visibility.Visible : Visibility.Collapsed;
        (target == CropTarget.Banner ? BannerTarget : IconTarget).IsChecked = true;
        Loaded += Window_Loaded;
        Closed += (_, _) =>
        {
            _previewTimer.Stop();
            _previewVersion++;
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Editor.Focus();
        try
        {
            _image = await Task.Run(() => Decode(_source));
        }
        catch (Exception error)
        {
            StateText.Text = $"Could not load the image: {error.Message}";
            StateText.Foreground = (Brush)FindResource("ErrorBrush");
            return;
        }
        SourceImage.Source = _image.Display;
        StateText.Visibility = Visibility.Collapsed;
        ZoomSlider.IsEnabled = ResetButton.IsEnabled = ApplyButton.IsEnabled = true;
        ResetView();
    }

    // Frame shape, title, and preview size for target; the view itself is set by the caller.
    private void SetTarget(CropTarget target)
    {
        _target = target;
        var banner = target == CropTarget.Banner;
        var checker = (Brush)FindResource("CheckerBrush");
        Title = banner ? "Crop banner" : "Crop icon";
        Frame.Width = _FRAME_HEIGHT * CropView.AspectOf(target);
        Frame.Background = banner ? checker : _iconFill;
        PreviewLabel.Text = banner ? "Banner output 256x128" : "Icon output 48x48, shown at 2x";
        PreviewBorder.Background = banner ? checker : null;
        PreviewImage.Width = banner ? 256 : 96;
        PreviewImage.Height = banner ? 128 : 96;
        RenderOptions.SetBitmapScalingMode(PreviewImage,
            banner ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
        PreviewImage.Source = null;
    }

    private void ResetView()
    {
        if (_image is not { } image) return;
        _view = CropView.Initial(image.Width, image.Height, _target,
            _target == _initialTarget ? _initialCrop : null);
        ShowView();
    }

    private void ShowView()
    {
        if (_image is not { } image) return;
        var scale = Frame.Width / _view.Width;
        SourceImage.Width = image.Width * scale;
        SourceImage.Height = image.Height * scale;
        Canvas.SetLeft(SourceImage, Frame.Width / 2 - _view.CenterX * scale);
        Canvas.SetTop(SourceImage, _FRAME_HEIGHT / 2 - _view.CenterY * scale);
        _syncingSlider = true;
        ZoomSlider.Value = _view.Zoom;
        _syncingSlider = false;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void Update(CropView view)
    {
        _view = view;
        ShowView();
    }

    // Pans by a distance in frame pixels.
    private void Pan(double x, double y)
    {
        var sourcePerPixel = _view.Width / Frame.Width;
        Update(_view.PanBy(x * sourcePerPixel, y * sourcePerPixel));
    }

    // Debounced off the UI thread; the same call the build makes, so it shows the build output.
    private async void RenderPreview()
    {
        _previewTimer.Stop();
        var version = ++_previewVersion;
        var (target, crop, source, background) = (_target, _view.Rect, _source, _iconBackground);
        try
        {
            var preview = await Task.Run(() => PngImage.Load(target == CropTarget.Banner
                ? ArtImages.RenderBanner(source, crop)
                : ArtImages.RenderIcon(source, crop, background)));
            if (version != _previewVersion) return;
            PreviewImage.Source = preview;
            PreviewError.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            if (version != _previewVersion) return;
            PreviewImage.Source = null;
            PreviewError.Text = $"Preview failed: {error.Message}";
            PreviewError.Visibility = Visibility.Visible;
        }
    }

    private void Target_Checked(object sender, RoutedEventArgs e)
    {
        var target = sender == IconTarget ? CropTarget.Icon : CropTarget.Banner;
        if (target == _target) return;
        SetTarget(target);
        if (_image is not null) Update(_view.WithTarget(target));
    }

    private void Editor_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Editor.Focus();
        if (_image is null) return;
        _dragFrom = e.GetPosition(Editor);
        Editor.CaptureMouse();
        e.Handled = true;
    }

    private void Editor_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        var at = e.GetPosition(Editor);
        _dragFrom = at;
        Pan(from.X - at.X, from.Y - at.Y);
    }

    private void Editor_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        Editor.ReleaseMouseCapture();

    private void Editor_LostMouseCapture(object sender, MouseEventArgs e) => _dragFrom = null;

    private void Editor_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_image is null) return;
        var at = e.GetPosition(Frame);
        Update(_view.ZoomTo(_view.Width * Math.Pow(_WHEEL_BASE, -e.Delta), at.X / Frame.Width,
            at.Y / _FRAME_HEIGHT));
        e.Handled = true;
    }

    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (_image is null) return;
        switch (e.Key)
        {
            case Key.Left: Pan(-_KEY_STEP, 0); break;
            case Key.Right: Pan(_KEY_STEP, 0); break;
            case Key.Up: Pan(0, -_KEY_STEP); break;
            case Key.Down: Pan(0, _KEY_STEP); break;
            case Key.OemPlus or Key.Add: Update(_view.ZoomTo(_view.Width / _KEY_ZOOM)); break;
            case Key.OemMinus or Key.Subtract: Update(_view.ZoomTo(_view.Width * _KEY_ZOOM)); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSlider || _image is null) return;
        Update(_view.ZoomTo(_view.WidthAt(e.NewValue)));
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e) => ResetView();

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_image is null) return;
        _result = new CropResult(_target, _view.Rect);
        DialogResult = true;
    }

    private sealed record LoadedImage(BitmapSource Display, int Width, int Height);

    // Decodes like ArtImages (Skia, EncodedOrigin applied) so Width/Height and the crop coordinates
    // match the build; the display copy is downscaled past _DISPLAY_LIMIT.
    private static LoadedImage Decode(string source)
    {
        using var codec = SKCodec.Create(source)
            ?? throw new InvalidDataException("Not a supported image file.");
        var info = codec.Info.WithColorType(SKColorType.Bgra8888).WithAlphaType(SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        var result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidDataException($"Decoding failed ({result}).");
        var origin = codec.EncodedOrigin;
        var swapped = origin >= SKEncodedOrigin.LeftTop;
        int width = swapped ? info.Height : info.Width, height = swapped ? info.Width : info.Height;
        var factor = Math.Min(1, (double)_DISPLAY_LIMIT / Math.Max(width, height));
        int displayWidth = Math.Max(1, (int)Math.Round(width * factor));
        int displayHeight = Math.Max(1, (int)Math.Round(height * factor));

        using var display = new SKBitmap(new SKImageInfo(displayWidth, displayHeight,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(display))
        using (var image = SKImage.FromBitmap(decoded))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(SKColors.Transparent);
            var scale = SKMatrix.CreateScale((float)displayWidth / width,
                (float)displayHeight / height);
            canvas.SetMatrix(scale.PreConcat(ArtImages.OriginMatrix(origin, width, height)));
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear,
                SKMipmapMode.Linear), paint);
        }
        var bitmap = BitmapSource.Create(displayWidth, displayHeight, 96, 96, PixelFormats.Pbgra32,
            null, display.GetPixels(), display.ByteCount, display.RowBytes);
        bitmap.Freeze();
        return new LoadedImage(bitmap, width, height);
    }
}

// The frame in source pixels (display orientation): center and width, height = width / aspect.
// Zoom runs from 8x past filling the frame out to 4x past showing the whole image; the frame center
// stays on the image, so the frame never loses it.
internal readonly record struct CropView(double ImageWidth, double ImageHeight, double Aspect,
    double CenterX, double CenterY, double Width)
{
    private const double _MAX_ZOOM_IN = 8, _MAX_ZOOM_OUT = 4;

    public static double AspectOf(CropTarget target) => target == CropTarget.Banner ? 2 : 1;

    public double Height => Width / Aspect;
    public double MinWidth => Math.Min(ImageWidth, ImageHeight * Aspect) / _MAX_ZOOM_IN;
    public double MaxWidth => Math.Max(ImageWidth, ImageHeight * Aspect) * _MAX_ZOOM_OUT;
    public CropRect Rect => new(CenterX - Width / 2, CenterY - Height / 2, Width, Height);
    // Slider position on a log scale: 0 = MaxWidth (zoomed out), 1 = MinWidth (zoomed in).
    public double Zoom => Math.Log(MaxWidth / Width) / Math.Log(MaxWidth / MinWidth);
    public double WidthAt(double zoom) => MaxWidth * Math.Pow(MinWidth / MaxWidth, zoom);

    // crop given: its center, the smallest frame that holds it. Else icon = centered square of the
    // shorter side, banner = whole image fitted.
    public static CropView Initial(double imageWidth, double imageHeight, CropTarget target,
        CropRect? crop)
    {
        var aspect = AspectOf(target);
        var view = new CropView(imageWidth, imageHeight, aspect, imageWidth / 2, imageHeight / 2, 0);
        if (crop is { } area)
            return view.Placed(area.Left + area.Width / 2, area.Top + area.Height / 2,
                Math.Max(area.Width, area.Height * aspect));
        return view.Placed(view.CenterX, view.CenterY, target == CropTarget.Icon
            ? Math.Min(imageWidth, imageHeight) : Math.Max(imageWidth, imageHeight * aspect));
    }

    // Zooms keeping the source point under frame fraction (x, y) still; 0.5, 0.5 = frame center.
    public CropView ZoomTo(double width, double x = 0.5, double y = 0.5)
    {
        var next = Math.Clamp(width, MinWidth, MaxWidth);
        return Placed(CenterX + (x - 0.5) * (Width - next),
            CenterY + (y - 0.5) * (Height - next / Aspect), next);
    }

    public CropView PanBy(double x, double y) => Placed(CenterX + x, CenterY + y, Width);

    // Same center and frame height, so the on-screen scale stays the same.
    public CropView WithTarget(CropTarget target)
    {
        var next = this with { Aspect = AspectOf(target) };
        return next.Placed(CenterX, CenterY, Height * next.Aspect);
    }

    private CropView Placed(double centerX, double centerY, double width) => this with
    {
        CenterX = Math.Clamp(centerX, 0, ImageWidth),
        CenterY = Math.Clamp(centerY, 0, ImageHeight),
        Width = Math.Clamp(width, MinWidth, MaxWidth),
    };
}
