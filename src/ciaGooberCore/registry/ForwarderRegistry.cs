using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CiaGoober.Core;

// Rom is the registry key: DS = canonical SD path, the other systems = ROM file name.
public sealed record RegistryEntry(string Name, GameSystem System, string Rom, int UniqueId);

// The shared title-ID registry (forwarders.json). IDs only grow and are never reused; unknown
// JSON fields round-trip unchanged. Keys compare with OrdinalIgnoreCase (Python used casefold).
public sealed class ForwarderRegistry
{
    private const int _FIRST_ID = 0xFF400;
    private const int _LAST_ID = 0xFF7FF;

    private static readonly TimeSpan _LOCK_TIMEOUT = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _LOCK_RETRY = TimeSpan.FromMilliseconds(50);
    private static readonly UTF8Encoding _UTF8 = new(false);
    // Matches Python json.dumps(indent=2, ensure_ascii=False) for the registry's content.
    private static readonly JsonSerializerOptions _JSON = new()
    {
        WriteIndented = true, IndentSize = 2, NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Snapshot _snapshot;

    private ForwarderRegistry(string path, Snapshot snapshot) => (_path, _snapshot) = (path, snapshot);

    // A missing file is a new, empty registry; the first reservation writes it.
    public static ForwarderRegistry Open(string path)
    {
        var full = Path.GetFullPath(path);
        return new(full, Load(full).Snapshot);
    }

    // Returns the known entry for this ROM, or adds one with the next ID and writes the file.
    // Re-reads the file under the lock so other instances and processes never get the same ID.
    public async Task<RegistryEntry> ReserveAsync(GameSystem system, string rom, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        rom = KeyRom(system, rom);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var fileLock = await LockAsync(_path + ".lock").ConfigureAwait(false);
            var (root, snapshot) = Load(_path);
            _snapshot = snapshot;
            if (snapshot.ByKey.TryGetValue(Key(system, rom), out var known)) return known;
            var id = snapshot.NextId;
            if (id > _LAST_ID)
                throw new InvalidOperationException("Forwarder title IDs 0xFF400-0xFF7FF are used up.");
            var game = new JsonObject { ["name"] = name };
            if (system != GameSystem.Nds) game["system"] = SystemName(system);
            game["rom"] = rom;
            game["uniqueId"] = $"0x{id:X}";
            game["banner"] = null;
            root["games"]!.AsArray().Add(game);
            root["nextUniqueId"] = $"0x{id + 1:X}";
            WriteAtomic(_path, root.ToJsonString(_JSON) + "\n");
            _snapshot = Validate(root);
            return _snapshot.ByKey[Key(system, rom)];
        }
        finally
        {
            _gate.Release();
        }
    }

    // The only way IDs restart at 0xFF400 (Settings > Reset Title IDs): moves the file to
    // backupFolder\forwarders-<yyyyMMdd-HHmmss>.json under the lock, so no reservation runs
    // meanwhile. Returns the backup path, or null when there was no file.
    public async Task<string?> ResetAsync(string backupFolder)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var fileLock = await LockAsync(_path + ".lock").ConfigureAwait(false);
            string? backup = null;
            if (File.Exists(_path))
            {
                Directory.CreateDirectory(backupFolder);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                backup = Path.Combine(backupFolder, $"forwarders-{stamp}.json");
                for (var n = 2; File.Exists(backup); n++)
                    backup = Path.Combine(backupFolder, $"forwarders-{stamp}-{n}.json");
                File.Move(_path, backup);
            }
            _snapshot = Load(_path).Snapshot;
            return backup;
        }
        finally
        {
            _gate.Release();
        }
    }

    public RegistryEntry? Find(GameSystem system, string rom) =>
        _snapshot.ByKey.GetValueOrDefault(Key(system, KeyRom(system, rom)));

    public IReadOnlyList<RegistryEntry> Entries() => _snapshot.Entries;

    private sealed record Snapshot(IReadOnlyList<RegistryEntry> Entries,
        Dictionary<string, RegistryEntry> ByKey, int NextId);

    // The registry's "system" value and key prefix.
    internal static string SystemName(GameSystem system) => system switch
    {
        GameSystem.Nds => "nds",
        GameSystem.Gba => "gba",
        GameSystem.Gb => "gb",
        GameSystem.Nes => "nes",
        GameSystem.Snes => "snes",
        _ => throw new ArgumentOutOfRangeException(nameof(system), system, null),
    };

    private static string KeyRom(GameSystem system, string rom) =>
        system == GameSystem.Nds ? SdPath.Canonical(rom) : SdPath.EmbeddedKey(rom, system);

    private static string Key(GameSystem system, string keyRom) => SystemName(system) + ":" + keyRom;

    private static (JsonObject Root, Snapshot Snapshot) Load(string path)
    {
        JsonNode? node;
        try
        {
            // FileShare.Delete lets another writer replace the file while it is being read.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            node = JsonNode.Parse(stream);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            node = new JsonObject { ["nextUniqueId"] = $"0x{_FIRST_ID:X}", ["games"] = new JsonArray() };
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Forwarder registry is not valid JSON: {path}", error);
        }
        if (node is not JsonObject root) throw new InvalidDataException("Forwarder registry must be an object.");
        return (root, Validate(root));
    }

    // Same checks as generator.py prepareRegistry.
    private static Snapshot Validate(JsonObject root)
    {
        if (root["games"] is not JsonArray games)
            throw new InvalidDataException("Invalid forwarder registry: games must be a list.");
        var entries = new List<RegistryEntry>(games.Count);
        var byKey = new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<int>();
        for (var i = 0; i < games.Count; i++)
        {
            if (games[i] is not JsonObject game || Text(game["name"]) is not { } name)
                throw new InvalidDataException($"Invalid forwarder registry game at index {i}.");
            string Where() => $"Forwarder registry game {i} ({name})";
            // DS entries predate the system field.
            var system = game.TryGetPropertyValue("system", out var systemNode)
                ? Text(systemNode) switch
                {
                    "nds" => GameSystem.Nds,
                    "gba" => GameSystem.Gba,
                    "gb" => GameSystem.Gb,
                    "nes" => GameSystem.Nes,
                    "snes" => GameSystem.Snes,
                    _ => throw new InvalidDataException($"{Where()}: unknown system."),
                }
                : GameSystem.Nds;
            string rom;
            try
            {
                rom = KeyRom(system, Text(game["rom"])
                    ?? throw new InvalidDataException($"{Where()}: rom must be a string."));
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException($"{Where()}: {error.Message}", error);
            }
            var id = ReadId(game["uniqueId"], _LAST_ID, Where());
            var entry = new RegistryEntry(name, system, rom, id);
            if (!byKey.TryAdd(Key(system, rom), entry) || !ids.Add(id))
                throw new InvalidDataException($"{Where()}: duplicate ROM path or unique ID.");
            foreach (var asset in (string[])["banner", "sound", "icon"])
                if (game[asset] is { } value && Text(value) is null or "")
                    throw new InvalidDataException($"{Where()}: invalid {asset} path.");
            if (game["iconBackground"] is { } background && !IconColor.IsValid(Text(background)))
                throw new InvalidDataException($"{Where()}: icon background must be #RRGGBB.");
            entries.Add(entry);
        }
        var next = ReadId(root["nextUniqueId"], _LAST_ID + 1, "Forwarder registry nextUniqueId");
        next = Math.Max(next, ids.Count == 0 ? _FIRST_ID : ids.Max() + 1);
        return new(entries, byKey, next);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    // Hex strings ("0xFF400") in the forwarder range; the Python reader also took any int literal.
    private static int ReadId(JsonNode? node, int maximum, string where)
    {
        var text = Text(node);
        if (text is not { Length: > 2 } || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                out var id))
            throw new InvalidDataException($"{where}: unique ID must be a hexadecimal string.");
        if (id < _FIRST_ID || id > maximum)
            throw new InvalidDataException($"{where}: unique ID is outside the forwarder range.");
        return id;
    }

    // OS-owned lock, released even when the process dies.
    private static async Task<FileStream> LockAsync(string lockPath)
    {
        var deadline = DateTime.UtcNow + _LOCK_TIMEOUT;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new IOException($"Another app is using the registry: {lockPath}", error);
            }
            await Task.Delay(_LOCK_RETRY).ConfigureAwait(false);
        }
    }

    private static void WriteAtomic(string path, string text)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(_UTF8.GetBytes(text));
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
