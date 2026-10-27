using System.Runtime.InteropServices;
using SkiaSharp;

namespace CiaGoober.Core;

// The build and the app previews both call these, so a preview is the exact build input.
public static class ArtImages
{
    private const int _BANNER_WIDTH = 256, _BANNER_HEIGHT = 128, _ICON_SIZE = 48;
    // Skia's cubic filter has no downscale prefilter, so it aliases (drops thin lines) below half
    // size; mipmaps average the source there instead. Catmull-Rom keeps 1:1 pixels exact.
    private static readonly SKSamplingOptions _FIT_SAMPLING = new(SKCubicResampler.CatmullRom);
    private static readonly SKSamplingOptions _SHRINK_SAMPLING =
        new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private static readonly SKSamplingOptions _ROM_ICON_SAMPLING =
        new(SKFilterMode.Linear, SKMipmapMode.None);

    // 256x128 RGBA PNG: the (cropped) image fitted whole, transparent padding.
    public static byte[] RenderBanner(string source, CropRect? crop)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(_BANNER_WIDTH, _BANNER_HEIGHT,
            SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            DrawFitted(canvas, source, crop, _BANNER_WIDTH, _BANNER_HEIGHT);
        }
        return EncodePng(bitmap);
    }

    // 48x48 opaque RGB PNG: the (cropped) image fitted whole on the #RRGGBB background.
    public static byte[] RenderIcon(string source, CropRect? crop, string background)
    {
        var fill = IconColor.Parse(background);
        using var bitmap = new SKBitmap(new SKImageInfo(_ICON_SIZE, _ICON_SIZE,
            SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(fill);
            DrawFitted(canvas, source, crop, _ICON_SIZE, _ICON_SIZE);
        }
        return EncodePng(bitmap);
    }

    // 48x48 opaque RGB PNG from a ROM icon; transparent pixels take the #RRGGBB background.
    public static byte[] RenderRomIcon(RgbaImage icon, string background)
    {
        var fill = IconColor.Parse(background);
        if (icon.Width <= 0 || icon.Height <= 0 || icon.Pixels.Length != icon.Width * icon.Height * 4)
            throw new ArgumentException("ROM icon pixels do not match its size.", nameof(icon));
        var pixels = new byte[icon.Pixels.Length];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            int alpha = icon.Pixels[i + 3];
            pixels[i] = Blend(icon.Pixels[i], fill.Red, alpha);
            pixels[i + 1] = Blend(icon.Pixels[i + 1], fill.Green, alpha);
            pixels[i + 2] = Blend(icon.Pixels[i + 2], fill.Blue, alpha);
            pixels[i + 3] = 255;
        }
        using var composited = new SKBitmap(new SKImageInfo(icon.Width, icon.Height,
            SKColorType.Rgba8888, SKAlphaType.Opaque));
        Marshal.Copy(pixels, 0, composited.GetPixels(), pixels.Length);
        using var resized = new SKBitmap(new SKImageInfo(_ICON_SIZE, _ICON_SIZE,
            SKColorType.Rgba8888, SKAlphaType.Opaque));
        if (!composited.ScalePixels(resized, _ROM_ICON_SAMPLING))
            throw new InvalidOperationException("Could not resize the ROM icon.");
        return EncodePng(resized);
    }

    private static byte Blend(byte color, byte fill, int alpha) =>
        (byte)((color * alpha + fill * (255 - alpha) + 127) / 255);

    // Matches Pillow's ImageOps.contain sizing, then centers like the old generator's paste.
    // One resample straight from the encoded pixels: EXIF orientation, crop, and scale are a
    // single matrix, and the clip keeps image pixels outside the crop out of the result.
    private static void DrawFitted(SKCanvas canvas, string source, CropRect? crop, int width,
        int height)
    {
        using var codec = SKCodec.Create(source)
            ?? throw new InvalidDataException($"Could not decode image: {source}");
        var info = codec.Info.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        var result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidDataException($"Could not decode image ({result}): {source}");
        using var image = SKImage.FromBitmap(decoded);

        var origin = codec.EncodedOrigin;
        var swapped = origin >= SKEncodedOrigin.LeftTop;
        int orientedWidth = swapped ? info.Height : info.Width;
        int orientedHeight = swapped ? info.Width : info.Height;
        var area = crop ?? new CropRect(0, 0, orientedWidth, orientedHeight);
        if (!(area.Width > 0 && area.Height > 0 && double.IsFinite(area.Left + area.Top + area.Width
            + area.Height)))
            throw new ArgumentException($"Invalid crop area: {area}", nameof(crop));

        int fitWidth = width, fitHeight = height;
        double areaRatio = area.Width / area.Height, targetRatio = (double)width / height;
        if (areaRatio > targetRatio)
            fitHeight = Math.Max(1, (int)Math.Round(area.Height / area.Width * width));
        else if (areaRatio < targetRatio)
            fitWidth = Math.Max(1, (int)Math.Round(area.Width / area.Height * height));
        int left = (width - fitWidth) / 2, top = (height - fitHeight) / 2;

        float scaleX = (float)(fitWidth / area.Width), scaleY = (float)(fitHeight / area.Height);
        var place = new SKMatrix(scaleX, 0, (float)(left - area.Left * scaleX),
            0, scaleY, (float)(top - area.Top * scaleY), 0, 0, 1);
        canvas.ClipRect(SKRect.Create(left, top, fitWidth, fitHeight));
        canvas.SetMatrix(place.PreConcat(OriginMatrix(origin, orientedWidth, orientedHeight)));
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, 0, 0, scaleX < 0.5f ? _SHRINK_SAMPLING : _FIT_SAMPLING, paint);
    }

    // Skia's SkEncodedOriginToMatrix: maps stored pixels to display orientation (w, h displayed).
    public static SKMatrix OriginMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new(0, -1, w, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new(0, -1, w, -1, 0, h, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new(0, 1, 0, -1, 0, h, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Could not encode PNG.");
        return data.ToArray();
    }
}
