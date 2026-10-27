using System.Text.RegularExpressions;

namespace CiaGoober.Core;

public sealed record LaunchBoxImageType(string Type, int Count);

// Image choice for the browser and the automatic picks (launchbox.js 107-169, game-list.js
// showPickerGame and _setScanArt).
public static partial class LaunchBoxImages
{
    public const string IMAGE_HOST = "https://images.launchbox-app.com/";
    public const string ANY_REGION = "Any region";
    public const string CLEAR_LOGO = "Clear Logo";
    public const string CART_FRONT = "Cart - Front";
    public const string BOX_FRONT = "Box - Front";
    private const string _SITE_SEARCH = "https://gamesdb.launchbox-app.com/games/results/";
    private static readonly string[] _TYPE_ORDER = [CLEAR_LOGO, CART_FRONT, BOX_FRONT];
    // GBA, GB and NES HOME icons use the box front in this fixed order, whatever the ROM's region
    // tag.
    private static readonly string[] _GBA_BOX_REGIONS =
        ["Europe", "North America", "World", "Japan", ANY_REGION];

    // Image names become cache file names, so only plain names are accepted.
    public static bool IsSafeName(string file) => SafeName().IsMatch(file);

    // Types with image counts: Clear Logo, Cart - Front, Box - Front, then the rest by name.
    public static IReadOnlyList<LaunchBoxImageType> Types(LaunchBoxGame game) =>
    [
        .. game.Images.GroupBy(image => image.Type)
            .Select(group => new LaunchBoxImageType(group.Key, group.Count()))
            .OrderBy(type => Array.IndexOf(_TYPE_ORDER, type.Type) is var at && at < 0
                ? int.MaxValue : at)
            .ThenBy(type => type.Type, StringComparer.InvariantCulture),
    ];

    // All images when type is null.
    public static IReadOnlyList<LaunchBoxImage> OfType(LaunchBoxGame game, string? type) =>
        [.. game.Images.Where(image => type is null || image.Type == type)];

    // Region filter values for a list of images; empty when there is only one region.
    public static IReadOnlyList<string> Regions(IEnumerable<LaunchBoxImage> images)
    {
        var regions = images.Select(image => image.Region).Distinct().ToArray();
        return regions.Length > 1 ? regions : [];
    }

    // The browser opens on the type the automatic pick for that target would use.
    public static string? DefaultType(LaunchBoxGame game, bool icon, GameSystem system)
    {
        string[] order = !icon ? [CLEAR_LOGO]
            : system != GameSystem.Nds ? [BOX_FRONT, CART_FRONT] : [CART_FRONT];
        var types = Types(game);
        return order.FirstOrDefault(type => types.Any(entry => entry.Type == type))
            ?? types.FirstOrDefault()?.Type;
    }

    public static LaunchBoxImage? DefaultBanner(LaunchBoxGame game, string romPath) =>
        Best(game, CLEAR_LOGO, RomRegionOrder(romPath));

    // GBA, GB and NES icons prefer the box front (Europe first), then the cart like DS.
    public static LaunchBoxImage? DefaultIcon(LaunchBoxGame game, RomInfo rom) =>
        (rom.System != GameSystem.Nds ? Best(game, BOX_FRONT, _GBA_BOX_REGIONS) : null)
        ?? Best(game, CART_FRONT, RomRegionOrder(rom.Path));

    // encodeURIComponent keeps !'()* unescaped; Uri.EscapeDataString does not.
    public static string SiteUrl(LaunchBoxGame game) => _SITE_SEARCH + Uri.EscapeDataString(game.Name)
        .Replace("%21", "!").Replace("%27", "'").Replace("%28", "(").Replace("%29", ")")
        .Replace("%2A", "*");

    // The image of one type in region order; the first image of the type when none matches.
    private static LaunchBoxImage? Best(LaunchBoxGame game, string type, IEnumerable<string> order)
    {
        var found = OfType(game, type);
        if (found.Count == 0) return null;
        return order.Select(region => found.FirstOrDefault(image => image.Region == region))
            .FirstOrDefault(image => image is not null) ?? found[0];
    }

    // The ROM's region tags in tag order ("(USA, Europe)": North America, Europe), then World,
    // North America, any region (launchbox.js tagRegions).
    private static IEnumerable<string> RomRegionOrder(string romPath)
    {
        var preferred = new List<string>();
        foreach (Match group in TagGroup().Matches(LaunchBoxMatcher.FileName(romPath)))
        {
            var tag = group.Groups[1].Value;
            var hits = new List<(int At, string Region)>();
            if (tag.StartsWith("USA", StringComparison.OrdinalIgnoreCase)
                || (tag + ",").Contains("USA,", StringComparison.OrdinalIgnoreCase))
                hits.Add((tag.IndexOf("USA", StringComparison.OrdinalIgnoreCase), "North America"));
            foreach (var (word, region) in new[] { ("Europe", "Europe"), ("Japan", "Japan") })
            {
                var at = tag.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                if (at >= 0) hits.Add((at, region));
            }
            preferred.AddRange(hits.OrderBy(hit => hit.At).Select(hit => hit.Region));
        }
        return preferred.Concat(["World", "North America", ANY_REGION]).Distinct();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*\.(?i:png|jpe?g)\z")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"\(([^)]*)\)")]
    private static partial Regex TagGroup();
}
