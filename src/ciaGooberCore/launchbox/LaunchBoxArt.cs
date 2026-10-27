namespace CiaGoober.Core;

// Indexed is false when no LaunchBox index exists for the system (nothing was looked up).
// A path is null when that target was not asked for, has no match, or failed (its error set).
public sealed record LaunchBoxArtResult(bool Indexed, LaunchBoxGame? Game, string? BannerPath,
    string? IconPath, Exception? BannerError, Exception? IconError);

public static class LaunchBoxArt
{
    // Match + default pick + download, shared by scans, Auto-Fill All and per-game Auto-Fill.
    // Banner = Clear Logo; icon = Cart - Front by the ROM's region (GBA: Box - Front first).
    public static async Task<LaunchBoxArtResult> AutoFillAsync(RomInfo rom, bool banner, bool icon,
        CancellationToken cancellationToken)
    {
        var index = banner || icon ? LaunchBoxIndex.Load(rom.System) : null;
        if (index is null) return new(false, null, null, null, null, null);
        var game = LaunchBoxMatcher.Match(index, rom);
        var logo = game is not null && banner ? LaunchBoxImages.DefaultBanner(game, rom.Path) : null;
        var cart = game is not null && icon ? LaunchBoxImages.DefaultIcon(game, rom) : null;
        var bannerTask = Place(logo, cancellationToken);
        var iconTask = Place(cart, cancellationToken);
        var (bannerPath, bannerError) = await bannerTask;
        var (iconPath, iconError) = await iconTask;
        return new(true, game, bannerPath, iconPath, bannerError, iconError);
    }

    private static async Task<(string? Path, Exception? Error)> Place(LaunchBoxImage? image,
        CancellationToken cancellationToken)
    {
        if (image is null) return (null, null);
        try
        {
            return (await LaunchBoxDownloads.ImageAsync(image.File, cancellationToken), null);
        }
        // A timeout is a TaskCanceledException too; only the caller's cancellation propagates.
        catch (Exception error) when (!cancellationToken.IsCancellationRequested
            && error is HttpRequestException or InvalidDataException or IOException
                or TaskCanceledException)
        {
            return (null, error);
        }
    }
}
