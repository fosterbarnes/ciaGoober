namespace CiaGoober.Core;

// Null Source means "not chosen": DS banner falls back to online art, GBA, GB, NES and SNES
// icons to the banner.
public sealed record ArtChoice(string? Source, CropRect? Crop);

// SaveType is "auto" or a save type name; Screen and Ghosting use the gbaVc.py preset names.
public sealed record GbaOptions(string SaveType, string Screen, string Ghosting);

// Everything one build needs; the app snapshots a game card into this. SdRomPath is the resolved
// "sd:/..." path a DS forwarder launches (SdPath.Resolve); null for the other systems. SnesPreset
// is the SNES preset the app chose (the card's, else the chip's from Settings); null = none.
public sealed record BuildJob(string RomPath, GameSystem System, string? SdRomPath, ArtChoice Banner,
    ArtChoice Icon, bool UseRomIcon, string IconBackground, string? SoundPath, string[] TitleLines,
    GbaOptions? Gba, ushort? SnesPreset, string OutputFolder);

public sealed record BuildResult(string OutputPath, int UniqueId);
