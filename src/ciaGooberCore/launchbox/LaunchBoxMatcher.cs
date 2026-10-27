using System.Text.RegularExpressions;

namespace CiaGoober.Core;

// Port of launchbox.js findGame (73-87) and its title helpers (46-69).
public static partial class LaunchBoxMatcher
{
    // The ROM's banner title (DS) or tag-stripped file name (GBA) is the last candidate, as the
    // old card name was.
    public static LaunchBoxGame? Match(LaunchBoxIndex index, RomInfo rom) =>
        Match(index, rom.Path, rom.TitleLines[0]);

    // No-Intro file names map through Files.xml; otherwise drop "(USA)"-style tags, then try the
    // base game a romhack names ("Hack (Base Game modification) (1.0)"), then the title.
    public static LaunchBoxGame? Match(LaunchBoxIndex index, string romPath, string title)
    {
        var name = FileName(romPath);
        var stem = Extension().Replace(name, "");
        var mapped = index.Files.GetValueOrDefault(stem);
        var stripped = TagsFromFirst().Replace(stem, "");
        var hack = HackBase().Match(stem);
        var hackBase = hack.Success ? hack.Groups[1].Value : null;
        string?[] candidates = [mapped, stripped, FrontArticle(stripped), hackBase,
            hackBase is null ? null : FrontArticle(hackBase), .. TitleCandidates(title)];
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            foreach (var key in TitleKeys(candidate))
                if (index.FindByKey(key) is { } game) return game;
        }
        return null;
    }

    // ROM banner titles carry trademark signs and a second line for the publisher or subtitle.
    public static string CleanTitle(string title) =>
        TrademarkSigns().Replace(title.Split('\n')[0], "").Trim();

    // path.win32.basename: both separators count on every OS.
    internal static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    private static string[] TitleKeys(string name)
    {
        var key = LaunchBoxIndex.NormalizeTitle(name);
        var shortKey = LaunchBoxIndex.ShortKey(name);
        return shortKey.Length > 0 && shortKey != key ? [key, shortKey] : [key];
    }

    // Three-line titles wrap the name over two lines, so try the lines before the publisher
    // joined, then the first line alone.
    private static string[] TitleCandidates(string title)
    {
        var lines = title.Split('\n');
        return lines.Length > 2
            ? [CleanTitle(string.Join(' ', lines[..^1])), CleanTitle(title)] : [CleanTitle(title)];
    }

    // No-Intro moves a leading "The" after the main title ("Legend of Zelda, The - Phantom
    // Hourglass"); restore it at the front.
    private static string? FrontArticle(string name)
    {
        var match = MovedArticle().Match(name);
        return match.Success
            ? match.Groups[2].Value + " " + match.Groups[1].Value + match.Groups[3].Value : null;
    }

    [GeneratedRegex(@"\.[^.]+$")]
    private static partial Regex Extension();

    [GeneratedRegex(@"\s*\(.*$")]
    private static partial Regex TagsFromFirst();

    [GeneratedRegex(@"\(([^()]+) modification\)", RegexOptions.IgnoreCase)]
    private static partial Regex HackBase();

    [GeneratedRegex(@"^(.*?),\s*(The|A|An)(\s+-\s+.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex MovedArticle();

    [GeneratedRegex("[™®©]")]
    private static partial Regex TrademarkSigns();
}
