using System.Text;
using System.Text.Json;

namespace CiaGoober.Core;

// Region is "Any region" when LaunchBox lists none.
public sealed record LaunchBoxImage(string Type, string Region, string File)
{
    public string Url => LaunchBoxImages.IMAGE_HOST + File;
}

public sealed record LaunchBoxGame(string Id, string Name, string? Year, IReadOnlyList<string> Alt,
    IReadOnlyList<LaunchBoxImage> Images);

// One system's index in the JSON format LaunchBoxIndexBuilder (launchbox.py) writes.
public sealed class LaunchBoxIndex
{
    private static readonly Dictionary<GameSystem, LaunchBoxIndex> _cache = [];
    private static readonly Lock _cacheLock = new();

    private readonly Dictionary<string, LaunchBoxGame> _byName = [];
    private readonly (string Name, string[] Alt)[] _searchKeys;

    public GameSystem System { get; }
    public IReadOnlyList<LaunchBoxGame> Games { get; }
    // No-Intro file name without extension -> LaunchBox game name (Files.xml).
    public IReadOnlyDictionary<string, string> Files { get; }

    private LaunchBoxIndex(GameSystem system, LaunchBoxGame[] games, Dictionary<string, string> files)
    {
        System = system;
        Games = games;
        Files = files;
        _searchKeys = [.. games.Select(game => (NormalizeTitle(game.Name),
            game.Alt.Select(NormalizeTitle).ToArray()))];
        // Exact names win over names without a trailing "Version" ("Pokémon Pearl Version" also
        // matches "Pokemon - Pearl (USA)"); primary names win over alternate names.
        foreach (var keyOf in new Func<string, string>[] { NormalizeTitle, ShortKey })
        {
            foreach (var alternate in new[] { false, true })
            {
                foreach (var game in games)
                {
                    foreach (var name in alternate ? game.Alt : [game.Name])
                    {
                        var key = keyOf(name);
                        if (key.Length > 0) _byName.TryAdd(key, game);
                    }
                }
            }
        }
    }

    public static string FileName(GameSystem system) => system switch
    {
        GameSystem.Nds => "index.json",
        GameSystem.Gba => "indexGba.json",
        GameSystem.Gb => "indexGb.json",
        GameSystem.Nes => "indexNes.json",
        _ => throw new ArgumentOutOfRangeException(nameof(system), system, null),
    };

    // The refreshed index in AppPaths.LaunchBox wins over the bundled seed; null when neither
    // exists. Loaded indexes stay cached until ClearCache; a missing one is not cached.
    public static LaunchBoxIndex? Load(GameSystem system)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(system, out var cached)) return cached;
            var path = new[] { AppPaths.LaunchBox, AppPaths.BundledLaunchBox }
                .Select(folder => Path.Combine(folder, FileName(system))).FirstOrDefault(File.Exists);
            if (path is null) return null;
            return _cache[system] = Read(path, system);
        }
    }

    // Deletes the refreshed index and downloaded art (AppPaths.LaunchBox); lookups use the
    // bundled index again and art downloads anew.
    public static void DeleteData()
    {
        lock (_cacheLock)
        {
            if (Directory.Exists(AppPaths.LaunchBox)) Directory.Delete(AppPaths.LaunchBox, true);
            _cache.Clear();
        }
    }

    public static void ClearCache()
    {
        lock (_cacheLock) _cache.Clear();
    }

    public static LaunchBoxIndex Read(string path, GameSystem system)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Invalid LaunchBox index: {path}");
        try
        {
            return new LaunchBoxIndex(system, [.. games.EnumerateArray().Select(ReadGame)],
                files.EnumerateObject().ToDictionary(file => file.Name, file => file.Value.GetString()!));
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException
            or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"Invalid LaunchBox index: {path}", error);
        }
    }

    // Games whose name (else an alternate name) contains the query: primary names first, then
    // earlier hits, then shorter names (launchbox.js searchGames).
    public IReadOnlyList<LaunchBoxGame> Search(string query, int limit = 20)
    {
        var key = NormalizeTitle(query);
        if (key.Length == 0) return [];
        var hits = new List<(LaunchBoxGame Game, int Primary, int At)>();
        for (var i = 0; i < Games.Count; i++)
        {
            var (name, alts) = _searchKeys[i];
            var at = name.IndexOf(key, StringComparison.Ordinal);
            if (at >= 0)
            {
                hits.Add((Games[i], 0, at));
                continue;
            }
            var altAt = alts.Select(alt => alt.IndexOf(key, StringComparison.Ordinal))
                .Where(found => found >= 0).DefaultIfEmpty(-1).Min();
            if (altAt >= 0) hits.Add((Games[i], 1, altAt));
        }
        return [.. hits.OrderBy(hit => hit.Primary).ThenBy(hit => hit.At)
            .ThenBy(hit => hit.Game.Name.Length).Take(limit).Select(hit => hit.Game)];
    }

    internal LaunchBoxGame? FindByKey(string key) => _byName.GetValueOrDefault(key);

    // Accents fold away, then everything but a-z0-9 (launchbox.js normalizeTitle).
    internal static string NormalizeTitle(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            if (c is >= '̀' and <= 'ͯ') continue;
            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(lower);
        }
        return builder.ToString();
    }

    internal static string ShortKey(string name)
    {
        var key = NormalizeTitle(name);
        return key.EndsWith("version", StringComparison.Ordinal) ? key[..^7] : key;
    }

    private static LaunchBoxGame ReadGame(JsonElement game)
    {
        var alt = game.TryGetProperty("alt", out var alts)
            ? alts.EnumerateArray().Select(name => name.GetString()!).ToArray() : [];
        var year = game.TryGetProperty("year", out var value) ? value.GetString() : null;
        // Unsafe names are dropped here, so every image can become a cache file name.
        var images = game.GetProperty("images").EnumerateArray()
            .Select(image => (Type: image[0].GetString()!, Region: image[1].GetString()!,
                File: image[2].GetString()!))
            .Where(image => LaunchBoxImages.IsSafeName(image.File))
            .Select(image => new LaunchBoxImage(image.Type,
                image.Region.Length > 0 ? image.Region : LaunchBoxImages.ANY_REGION, image.File));
        return new LaunchBoxGame(game.GetProperty("id").GetString()!,
            game.GetProperty("name").GetString()!, year, alt, [.. images]);
    }
}
