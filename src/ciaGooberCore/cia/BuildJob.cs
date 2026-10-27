namespace CiaGoober.Core;

// Null Source means "not chosen": DS banner falls back to online art, GBA and GB icons to the
// banner.
public sealed record ArtChoice(string? Source, CropRect? Crop);

// SaveType is "auto" or a save type name; Screen and Ghosting use the gbaVc.py preset names.
public sealed record GbaOptions(string SaveType, string Screen, string Ghosting);

// Everything one build needs; the app snapshots a game card into this.
public sealed record BuildJob(string RomPath, GameSystem System, string? CustomSdPath, ArtChoice Banner,
    ArtChoice Icon, bool UseRomIcon, string IconBackground, string? SoundPath, string[] TitleLines,
    GbaOptions? Gba, string OutputFolder, string? SdRomFolder);

public sealed record BuildResult(string OutputPath, int UniqueId);
