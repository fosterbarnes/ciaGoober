using System.IO.Enumeration;

namespace CiaGoober.Core;

// Based on YANBF-GUI/rom-scan.js scanRoms (lines 9-30). Systems are told apart by extension
// alone, so any folder layout works (No-Intro sets, ES-DE or RetroPie roms\gba, flat).
public static class RomScanner
{
    private const string _BIOS_FOLDER = "BIOS";
    private const string _BIOS_PREFIX = "[BIOS]";
    private const string _APPLE_DOUBLE_PREFIX = "._";

    // Ordered for dialog filters and messages.
    private static readonly (string Extension, GameSystem System)[] _EXTENSIONS =
    [
        (".nds", GameSystem.Nds),
        (".gba", GameSystem.Gba), (".agb", GameSystem.Gba),
        (".gb", GameSystem.Gb), (".gbc", GameSystem.Gb), (".sgb", GameSystem.Gb),
        (".nes", GameSystem.Nes),
    ];

    // Reparse points (symlinks, junctions) and System entries (System Volume Information,
    // $RECYCLE.BIN on a drive root) are skipped and not recursed into. Unreadable folders throw,
    // as readdirSync does.
    private static readonly EnumerationOptions _OPTIONS = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        IgnoreInaccessible = false,
    };

    // Recursive scan for one system's ROMs; null system means every system. Skips BIOS folders,
    // No-Intro "[BIOS] ..." files and macOS "._" AppleDouble files.
    public static IReadOnlyList<string> Scan(string folder, GameSystem? system)
    {
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Not a directory: {folder}");
        var found = new FileSystemEnumerable<string>(root,
            (ref FileSystemEntry entry) => entry.ToFullPath(), _OPTIONS)
        {
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                !entry.FileName.Equals(_BIOS_FOLDER, StringComparison.OrdinalIgnoreCase),
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory
                && !entry.FileName.StartsWith(_BIOS_PREFIX, StringComparison.OrdinalIgnoreCase)
                && !entry.FileName.StartsWith(_APPLE_DOUBLE_PREFIX, StringComparison.Ordinal)
                && IsRom(Path.GetExtension(entry.FileName).ToString(), system),
        }.ToList();
        // localeCompare with the default locale.
        found.Sort(StringComparer.CurrentCulture);
        return found;
    }

    // The system a ROM extension (with the dot) belongs to, or null for any other file.
    public static GameSystem? SystemOf(string extension)
    {
        foreach (var (known, system) in _EXTENSIONS)
            if (known.Equals(extension, StringComparison.OrdinalIgnoreCase)) return system;
        return null;
    }

    // ROM extensions (with the dot) of one system; null system means every system.
    public static IReadOnlyList<string> Extensions(GameSystem? system) => _EXTENSIONS
        .Where(entry => system is null || entry.System == system)
        .Select(entry => entry.Extension).ToList();

    // Deepest folder holding every file, or null for no files or files on different drives.
    public static string? CommonFolder(IEnumerable<string> files)
    {
        string? common = null;
        foreach (var file in files)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
            if (common is null)
            {
                common = folder;
                continue;
            }
            while (!IsWithin(folder, common))
            {
                common = Path.GetDirectoryName(common);
                if (common is null) return null;
            }
        }
        return common;
    }

    internal static bool IsRom(string extension, GameSystem? system) =>
        SystemOf(extension) is { } found && (system is null || found == system);

    private static bool IsWithin(string folder, string parent) =>
        folder.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || folder.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent
            : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
