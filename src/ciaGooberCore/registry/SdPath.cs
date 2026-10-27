using System.Text;
using System.Text.RegularExpressions;

namespace CiaGoober.Core;

public static partial class SdPath
{
    private const string _INVALID_CHARS = "\\:*?\"<>|";
    private const string _NO_DRIVE = "Set the DS ROM Folder in Settings or the SD ROM path on the card";

    // Canonical "sd:/..." form of a DS ROM path on the SD card; throws on an invalid path.
    public static string Canonical(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Normalize(NormalizationForm.FormC);
        if (value.StartsWith('/')) value = "sd:" + value;
        if (value.Length < 4 || value[..4].ToLowerInvariant() != "sd:/")
            throw new ArgumentException($"ROM path must start with sd:/ or /: {value}");
        value = "sd:/" + value[4..];
        var parts = value[4..].Split('/');
        if (Encoding.UTF8.GetByteCount(value) > 255
            || parts.Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' '))
            || value[4..].Any(c => char.IsControl(c) || _INVALID_CHARS.Contains(c))
            || parts[^1].EnumerateRunes().Count() <= 4
            || !value.ToLowerInvariant().EndsWith(".nds", StringComparison.Ordinal))
            throw new ArgumentException(
                $"Invalid SD ROM path (maximum 255 UTF-8 bytes, .nds required): {value}");
        return value;
    }

    // Registry key of a GBA or GB ROM: its file name only, since the ROM is embedded in the CIA.
    public static string EmbeddedKey(string romPath, GameSystem system)
    {
        ArgumentNullException.ThrowIfNull(romPath);
        var value = Path.GetFileName(romPath).Normalize(NormalizationForm.FormC);
        var extension = Path.GetExtension(value).ToLowerInvariant();
        if (system == GameSystem.Nds
            || value.EnumerateRunes().Count() <= extension.Length
            || !RomScanner.IsRom(extension, system)
            || value.AsSpan().IndexOfAny("/\\:") >= 0)
            throw new ArgumentException(
                $"Invalid {(system == GameSystem.Gba ? "GBA" : "Game Boy")} ROM file name: {value}");
        return value;
    }

    // The DS path TWiLight opens: card override, else SD ROM folder + file name, else the ROM's
    // own drive (never C:).
    public static string Resolve(string pcRomPath, string? customSdPath, string? sdRomFolder)
    {
        var custom = customSdPath?.Trim() ?? "";
        if (custom != "") return Canonical(Normalize(custom));
        var (full, root) = SplitDrive(pcRomPath);
        var folder = Normalize(sdRomFolder?.Trim() ?? "");
        if (folder != "")
        {
            if (!folder.StartsWith("sd:/", StringComparison.Ordinal))
                throw new ArgumentException("DS ROM Folder must start with sd:/ or /");
            return Canonical(Normalize(folder.TrimEnd('/') + "/" + Path.GetFileName(full)));
        }
        if (root is null || char.ToUpperInvariant(root[0]) == 'C') throw new ArgumentException(_NO_DRIVE);
        return Canonical(Normalize("sd:/" + full[root.Length..]));
    }

    // A folder picked on the inserted card (J:\roms\nds) as "sd:/roms/nds"; the drive root is
    // "sd:/". C: and paths without a drive letter are not a card and throw.
    public static string FromCardFolder(string folderOnCard)
    {
        var (full, root) = SplitDrive(folderOnCard);
        if (root is null || char.ToUpperInvariant(root[0]) == 'C')
            throw new ArgumentException($"Pick a folder on the SD card drive: {folderOnCard}");
        return "sd:/" + Normalize(full[root.Length..]).Trim('/');
    }

    // Console paths use forward slashes only, so Windows-style input is converted.
    private static string Normalize(string sdPath)
    {
        sdPath = SeparatorRuns().Replace(sdPath.Normalize(NormalizationForm.FormC), "/");
        if (sdPath.StartsWith("sd:", StringComparison.OrdinalIgnoreCase)) sdPath = "sd:" + sdPath[3..];
        return sdPath.StartsWith('/') ? "sd:" + sdPath : sdPath;
    }

    // Full path plus its "X:\" root, or a null root when the path is not on a drive letter.
    private static (string Full, string? Root) SplitDrive(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Path.IsPathFullyQualified(path)) return (path, null);
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return (full, root is { Length: 3 } && char.IsAsciiLetter(root[0]) && root[1..] == ":\\"
            ? root : null);
    }

    [GeneratedRegex(@"[\\/]+")]
    private static partial Regex SeparatorRuns();
}
