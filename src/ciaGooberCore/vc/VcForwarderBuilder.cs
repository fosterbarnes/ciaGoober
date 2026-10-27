using System.Buffers.Binary;

namespace CiaGoober.Core;

public static class VcForwarderBuilder
{
    // A game without patches; Pokemon titles ship link-cable patches that fit only their game.
    private static readonly byte[] _EMPTY_PATCH = ";Format Sample\r\n;no patches for this game\r\n"u8
        .ToArray();
    private const int _SAVE_DATA_SIZE_OFFSET = 0x1C0;
    // Room for the cartridge RAM, a backup copy and the clock (vcinject gbvc.py save_size).
    private const int _SAVE_OVERHEAD = 0x1000;

    // The CXI that makerom wraps with -content: the template's emulator and RomFS with this ROM
    // in place of the donor's game, the bannertool banner and SMDH. snesPreset is the SNES
    // preset the app chose (the card's, else the chip's from Settings).
    public static byte[] BuildCxi(GameSystem system, string romPath, byte[] banner, byte[] smdh,
        VcTemplate template, int uniqueId, string gameCode, ushort? snesPreset,
        IProgress<string> log)
    {
        var files = new Dictionary<string, byte[]>(template.Files, StringComparer.Ordinal);
        int ramSize;
        if (system == GameSystem.Snes)
        {
            var image = SnesRom.Parse(File.ReadAllBytes(romPath));
            var header = template.SnesHeader
                ?? throw new InvalidDataException("Not an SNES template.");
            var preset = SnesPreset(image.Chip, snesPreset);
            log.Report($"Chip: {SnesRom.ChipName(image.Chip)}; preset ID 0x{preset:X4}");
            files[template.RomName] = SnesRom.ToDataBin(image, header, preset);
            files[template.SnesIcon!] = smdh;
            ramSize = image.SramSize;
        }
        else
        {
            (files[template.RomName], ramSize) = GameImage(system, romPath, log);
            files["/" + Path.GetFileName(template.RomName) + ".patch"] = _EMPTY_PATCH;
        }
        var saveSize = SaveDataSize(template.Exheader, ramSize);
        log.Report($"Cartridge RAM: {ramSize} bytes; save data: {saveSize} bytes");

        var titleId = 0x0004000000000000UL | (ulong)uniqueId << 8;
        var exheader = Ncch.PatchExheader(template.Exheader, titleId, gameCode, saveSize);
        var exefs = Ncch.BuildExefs((".code", template.Code), ("banner", banner), ("icon", smdh),
            ("logo", template.Logo));
        return Ncch.BuildNcch(titleId, $"CTR-H-{gameCode}", exheader, exefs, RomFs.Build(files));
    }

    // GB and NES: the game file the emulator reads and its cartridge RAM in bytes: GB ROMs as
    // they are, NES ROMs converted to TNES.
    private static (byte[] Rom, int RamSize) GameImage(GameSystem system, string romPath,
        IProgress<string> log)
    {
        var rom = File.ReadAllBytes(romPath);
        switch (system)
        {
            case GameSystem.Gb:
                GbRom.CheckRomSize(rom.Length);
                if (!GbRom.HeaderValid(rom))
                    log.Report("Warning: the ROM header checksum is invalid (corrupt ROM or ROM "
                        + "hack).");
                return (rom, GbRom.RamSize(rom));
            case GameSystem.Nes:
                return NesRom.ToTnes(rom, log);
            default:
                throw new ArgumentOutOfRangeException(nameof(system), system, null);
        }
    }

    // The chosen preset, else the generic one for a plain cart. A chip game without a preset has
    // no known way to run.
    internal static ushort SnesPreset(SnesChip chip, ushort? chosen)
    {
        if (chosen is { } preset) return preset;
        if (chip == SnesChip.None) return SnesRom.GENERIC_PRESET;
        throw new InvalidOperationException($"No preset for {SnesRom.ChipLabel(chip)} games: set "
            + "it in Settings > SNES chip presets or in the game's Preset ID.");
    }

    // The donor's save data size, doubled until the cartridge RAM fits twice plus overhead.
    internal static ulong SaveDataSize(byte[] exheader, int ramSize)
    {
        var size = BinaryPrimitives.ReadUInt64LittleEndian(exheader.AsSpan(_SAVE_DATA_SIZE_OFFSET));
        var needed = 2UL * (ulong)ramSize + _SAVE_OVERHEAD;
        if (size == 0) size = _SAVE_OVERHEAD;
        while (size < needed) size *= 2;
        return size;
    }
}
