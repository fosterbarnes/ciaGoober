using System.Net;
using System.Text;

namespace CiaGoober.Core;

// DS-only build steps: online banner and sound fallback, romfs and the makerom command.
internal static class DsForwarderBuilder
{
    private const string _ASSETS_URL =
        "https://raw.githubusercontent.com/YANBForwarder/assets/main/assets";
    private const string _GAMETDB_URL = "https://art.gametdb.com/ds/coverM";
    private static readonly HttpClient _HTTP = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly UTF8Encoding _UTF8 = new(false);
    // GameTDB region folder by the game code's region letter (generator.py:339-352).
    private static readonly Dictionary<char, string> _GAMETDB_REGIONS = new()
    {
        ['D'] = "DE", ['E'] = "US", ['F'] = "FR", ['H'] = "NL", ['I'] = "IT", ['J'] = "JA",
        ['K'] = "KO", ['R'] = "RU", ['S'] = "ES", ['T'] = "US", ['U'] = "AU", ['#'] = "HB",
    };

    // YANBF assets by game code then 3-character code, then GameTDB box art by region then EN
    // (generator.py:315-325, 337-363). Returns the downloaded file, or null when none exists.
    public static async Task<string?> DownloadBannerAsync(string gameCode, string jobFolder,
        CancellationToken ct)
    {
        var regions = new[] { _GAMETDB_REGIONS.GetValueOrDefault(gameCode[3], "EN"), "EN" }.Distinct();
        return await DownloadAsync(Path.Combine(jobFolder, "assetBanner.png"),
                AssetUrls(gameCode, ".png"), ct).ConfigureAwait(false)
            ?? await DownloadAsync(Path.Combine(jobFolder, "boxart.jpg"),
                regions.Select(region => $"{_GAMETDB_URL}/{region}/{gameCode}.jpg"), ct).ConfigureAwait(false);
    }

    // YANBF asset sound by game code then 3-character code (generator.py:326-334).
    public static Task<string?> DownloadSoundAsync(string gameCode, string jobFolder,
        CancellationToken ct) =>
        DownloadAsync(Path.Combine(jobFolder, "sound.wav"), AssetUrls(gameCode, ".wav"), ct);

    // The SD path TWiLight opens; forwarder.elf reads romfs:/path.txt (generator.py:487-495).
    public static void WriteRomFs(string jobFolder, string sdPath)
    {
        var romfs = Directory.CreateDirectory(Path.Combine(jobFolder, "romfs")).FullName;
        File.WriteAllText(Path.Combine(romfs, "path.txt"), sdPath, _UTF8);
    }

    // generator.py:523-530 with job-folder names.
    public static string[] MakeromArguments(string title, string gameCode, int uniqueId) =>
    [
        "-f", "cia", "-target", "t", "-exefslogo", "-rsf", "build-cia.rsf", "-elf",
        "forwarder.elf", "-banner", "banner.bin", "-icon", "output.smdh", "-DAPP_ROMFS=romfs",
        "-major", "1", "-minor", "6", "-micro", "3", "-DAPP_VERSION_MAJOR=1", "-o", "build.cia",
        $"-DAPP_PRODUCT_CODE=CTR-H-{gameCode}", $"-DAPP_TITLE={title}",
        $"-DAPP_UNIQUE_ID={uniqueId}",
    ];

    private static IEnumerable<string> AssetUrls(string gameCode, string extension) =>
        [$"{_ASSETS_URL}/{gameCode}/{gameCode}{extension}",
            $"{_ASSETS_URL}/{gameCode[..3]}/{gameCode[..3]}{extension}"];

    // Saves the first URL that answers 200 OK. An unreachable URL (offline, timeout) counts as
    // having nothing, so the caller's own fallback applies.
    private static async Task<string?> DownloadAsync(string path, IEnumerable<string> urls,
        CancellationToken ct)
    {
        foreach (var url in urls)
        {
            byte[] bytes;
            try
            {
                using var response = await _HTTP.GetAsync(url, ct).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK) continue;
                bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException
                || error is TaskCanceledException && !ct.IsCancellationRequested)
            {
                continue;
            }
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            return path;
        }
        return null;
    }
}
