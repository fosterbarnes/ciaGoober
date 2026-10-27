using System.IO.Enumeration;

namespace CiaGoober.Core;

public sealed record SdRom(string SdPath, NdsIdentity Identity);

public enum SdMatchKind { Mapped, Ambiguous, Missing }

// Mapped: SdPath is the card file. Ambiguous: Candidates are the card files that could be it.
public sealed record SdMatch(SdMatchKind Kind, string? SdPath, IReadOnlyList<string> Candidates);

// Every DS ROM on one SD card, read once per card refresh, so PC ROMs can be matched to the card
// file a forwarder must launch whatever its name or folder.
public sealed class SdRomIndex
{
    // Card system and homebrew folders; their .nds files are apps, not games.
    private static readonly HashSet<string> _SKIPPED_FOLDERS = new(StringComparer.OrdinalIgnoreCase)
    {
        "_nds", "Nintendo 3DS", "3ds", "luma", "gm9", "DCIM",
    };

    private static readonly EnumerationOptions _OPTIONS = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        IgnoreInaccessible = false,
    };

    private SdRomIndex(string cardRoot, IReadOnlyList<SdRom> roms)
    {
        CardRoot = cardRoot;
        Roms = roms;
    }

    public string CardRoot { get; }
    public IReadOnlyList<SdRom> Roms { get; }

    // Files whose header cannot be read or whose path is not a valid SD path are left out: no
    // forwarder can launch them. Folder read errors throw.
    public static SdRomIndex Build(string cardRoot)
    {
        var root = Path.GetFullPath(cardRoot);
        var files = new FileSystemEnumerable<string>(root,
            (ref FileSystemEntry entry) => entry.ToFullPath(), _OPTIONS)
        {
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                !_SKIPPED_FOLDERS.Contains(entry.FileName.ToString()),
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory
                && !entry.FileName.StartsWith("._", StringComparison.Ordinal)
                && RomScanner.SystemOf(Path.GetExtension(entry.FileName).ToString())
                    == GameSystem.Nds,
        }.ToList();
        var roms = new List<SdRom>();
        foreach (var file in files)
        {
            try
            {
                roms.Add(new SdRom(SdPath.FromCardFile(root, file), NdsRom.ReadIdentity(file)));
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
            }
        }
        return new SdRomIndex(root, roms);
    }

    // The card file at this SD path (the card's file system ignores case), or null.
    public SdRom? Find(string sdPath) => Roms.FirstOrDefault(rom =>
        string.Equals(rom.SdPath, sdPath, StringComparison.OrdinalIgnoreCase));

    // Same file name and identity first, then a single file with the identity under any name.
    // A same-named file with another identity is never a match.
    public SdMatch Match(string pcRomPath, NdsIdentity identity)
    {
        var same = Roms.Where(rom => rom.Identity == identity).ToList();
        var name = Path.GetFileName(pcRomPath);
        var named = same.Where(rom => string.Equals(rom.SdPath[(rom.SdPath.LastIndexOf('/') + 1)..],
            name, StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = named.Count > 0 ? named : same;
        return candidates.Count switch
        {
            0 => new SdMatch(SdMatchKind.Missing, null, []),
            1 => new SdMatch(SdMatchKind.Mapped, candidates[0].SdPath, []),
            _ => new SdMatch(SdMatchKind.Ambiguous, null,
                candidates.Select(rom => rom.SdPath).ToList()),
        };
    }
}
