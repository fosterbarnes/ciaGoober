namespace CiaGoober.Core;

// Port of YANBF-GUI/rom-scan.js scanRoms (lines 9-30), minus the .dsi/.ids extensions.
public static class RomScanner
{
    // Hidden and system entries are kept like rom-scan.js; reparse points (symlinks, junctions) are
    // skipped and not recursed into. Unreadable folders throw, as readdirSync does.
    private static readonly EnumerationOptions _OPTIONS = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
        MatchType = MatchType.Simple,
    };

    // Recursive scan for one system's ROMs; null system means every system.
    public static IReadOnlyList<string> Scan(string folder, GameSystem? system)
    {
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Not a directory: {folder}");
        var found = Directory.EnumerateFiles(root, "*", _OPTIONS)
            .Where(file => IsRom(Path.GetExtension(file), system)).ToList();
        // localeCompare with the default locale.
        found.Sort(StringComparer.CurrentCulture);
        return found;
    }

    // The system a ROM extension (with the dot) belongs to, or null for any other file.
    public static GameSystem? SystemOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".nds" => GameSystem.Nds,
        ".gba" => GameSystem.Gba,
        ".gb" or ".gbc" => GameSystem.Gb,
        ".nes" => GameSystem.Nes,
        _ => null,
    };

    internal static bool IsRom(string extension, GameSystem? system) =>
        SystemOf(extension) is { } found && (system is null || found == system);
}
