using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CiaGoober.Core;

public static partial class OutputNaming
{
    // Registered name when known, else ROM stem + "-" + 10 hex of SHA-256(key), so rebuilds of the
    // same game overwrite the same CIA. The key is lowercased like the old app's canonicalRomKey.
    public static string CiaPath(string folder, RegistryEntry? known, GameSystem system, string rom,
        string romPath)
    {
        var stem = known?.Name ?? Path.GetFileNameWithoutExtension(romPath);
        var name = UnsafeChars().Replace(stem, "_").Trim();
        if (known is null)
        {
            var key = system == GameSystem.Nds ? SdPath.Canonical(rom)
                : ForwarderRegistry.SystemName(system) + ":" + SdPath.EmbeddedKey(rom, system);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant()));
            name += "-" + Convert.ToHexStringLower(hash)[..10];
        }
        return Path.Combine(folder, name + ".cia");
    }

    [GeneratedRegex("[<>:\"/\\\\|?*\\x00-\\x1f]")]
    private static partial Regex UnsafeChars();
}
