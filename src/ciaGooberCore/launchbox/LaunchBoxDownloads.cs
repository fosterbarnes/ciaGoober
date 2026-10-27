using System.Collections.Concurrent;
using SkiaSharp;

namespace CiaGoober.Core;

// Disk-cached LaunchBox images. Each target file is fetched by one shared task, so callers on
// several threads never download the same file twice; a caller's cancellation stops its wait,
// not the shared download. Files appear only complete (temp file, then move).
public static class LaunchBoxDownloads
{
    private const int _THUMB_WIDTH = 256;
    private static readonly SKSamplingOptions _THUMB_SAMPLING =
        new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> _active =
        new(StringComparer.OrdinalIgnoreCase);

    internal static HttpClient Http { get; } = CreateClient();

    public static Task<string> ImageAsync(string file, CancellationToken cancellationToken) =>
        ImageAsync(file, AppPaths.LaunchBoxImages, cancellationToken);

    public static Task<string> ThumbnailAsync(string file, CancellationToken cancellationToken) =>
        ThumbnailAsync(file, AppPaths.LaunchBoxThumbs, AppPaths.LaunchBoxImages, cancellationToken);

    // The full image, cached under its LaunchBox name in folder.
    public static Task<string> ImageAsync(string file, string folder,
        CancellationToken cancellationToken) =>
        Cached(Path.Combine(folder, CheckedName(file)), async target =>
            await WriteAtomic(target, await Fetch(file)), cancellationToken);

    // A PNG at most 256 px wide in folder, made from the cached full image in imageFolder when
    // there is one, else from a download that is not kept.
    public static Task<string> ThumbnailAsync(string file, string folder, string imageFolder,
        CancellationToken cancellationToken) =>
        Cached(Path.Combine(folder, Path.ChangeExtension(CheckedName(file), ".png")),
            async target =>
            {
                var full = Path.Combine(imageFolder, file);
                var bytes = File.Exists(full) ? await File.ReadAllBytesAsync(full) : await Fetch(file);
                await WriteAtomic(target, Thumbnail(bytes, file));
            }, cancellationToken);

    private static async Task<string> Cached(string target, Func<string, Task> create,
        CancellationToken cancellationToken)
    {
        if (File.Exists(target)) return target;
        cancellationToken.ThrowIfCancellationRequested();
        var lazy = _active.GetOrAdd(target, key => new Lazy<Task<string>>(() => Run(key, create)));
        return await lazy.Value.WaitAsync(cancellationToken);
    }

    private static async Task<string> Run(string target, Func<string, Task> create)
    {
        try
        {
            if (!File.Exists(target)) await create(target);
            return target;
        }
        finally
        {
            _active.TryRemove(target, out _);
        }
    }

    // HttpClient.Timeout covers the whole buffered body here.
    private static async Task<byte[]> Fetch(string file)
    {
        using var response = await Http.GetAsync(LaunchBoxImages.IMAGE_HOST + file);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"LaunchBox image download failed with HTTP {(int)response.StatusCode}: {file}",
                null, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length == 0) throw new InvalidDataException($"LaunchBox image is empty: {file}");
        return bytes;
    }

    private static byte[] Thumbnail(byte[] bytes, string file)
    {
        using var full = SKBitmap.Decode(bytes)
            ?? throw new InvalidDataException($"LaunchBox image could not be decoded: {file}");
        var width = Math.Min(_THUMB_WIDTH, full.Width);
        var height = Math.Max(1, (int)Math.Round((double)full.Height * width / full.Width));
        using var small = full.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888,
            SKAlphaType.Premul), _THUMB_SAMPLING)
            ?? throw new InvalidDataException($"LaunchBox image could not be resized: {file}");
        using var data = small.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException($"LaunchBox thumbnail could not be encoded: {file}");
        return data.ToArray();
    }

    private static async Task WriteAtomic(string target, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = $"{target}.{Environment.ProcessId}.part";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string CheckedName(string file) => LaunchBoxImages.IsSafeName(file) ? file
        : throw new ArgumentException($"Invalid LaunchBox image name: {file}", nameof(file));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var version = typeof(LaunchBoxDownloads).Assembly.GetName().Version!.ToString(3);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ciaGoober/{version}");
        return client;
    }
}
