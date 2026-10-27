using System.Buffers.Binary;

namespace CiaGoober.Core;

public static class GbForwarderBuilder
{
    // A game without patches; Pokemon titles ship link-cable patches that fit only their game.
    private static readonly byte[] _EMPTY_PATCH = ";Format Sample\r\n;no patches for this game\r\n"u8
        .ToArray();
    private const int _SAVE_DATA_SIZE_OFFSET = 0x1C0;
    // Room for the cartridge RAM, a backup copy and the clock (vcinject gbvc.py save_size).
    private const int _SAVE_OVERHEAD = 0x1000;

    // The CXI that makerom wraps with -content: the template's emulator and RomFS with this ROM
    // in place of the donor's game, the bannertool banner and SMDH.
    public static byte[] BuildCxi(string romPath, byte[] banner, byte[] smdh, GbTemplate template,
        int uniqueId, string gameCode, IProgress<string> log)
    {
        var rom = File.ReadAllBytes(romPath);
        GbRom.CheckRomSize(rom.Length);
        if (!GbRom.HeaderValid(rom))
            log.Report("Warning: the ROM header checksum is invalid (corrupt ROM or ROM hack).");
        var files = new Dictionary<string, byte[]>(template.Files, StringComparer.Ordinal)
        {
            [template.RomName] = rom,
            ["/" + Path.GetFileName(template.RomName) + ".patch"] = _EMPTY_PATCH,
        };
        var ramSize = GbRom.RamSize(rom);
        var saveSize = SaveDataSize(template.Exheader, ramSize);
        log.Report($"Cartridge RAM: {ramSize} bytes; save data: {saveSize} bytes");

        var titleId = 0x0004000000000000UL | (ulong)uniqueId << 8;
        var exheader = Ncch.PatchExheader(template.Exheader, titleId, gameCode, saveSize);
        var exefs = Ncch.BuildExefs((".code", template.Code), ("banner", banner), ("icon", smdh),
            ("logo", template.Logo));
        return Ncch.BuildNcch(titleId, $"CTR-H-{gameCode}", exheader, exefs, RomFs.Build(files));
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
