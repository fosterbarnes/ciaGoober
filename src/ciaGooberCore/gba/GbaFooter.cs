using System.Buffers.Binary;

namespace CiaGoober.Core;

// ExeFS .code for AGB_FIRM: the ROM padded to 16 bytes, then a 0x360-byte footer of metadata
// (0x324), padding, ROM and metadata descriptors (0x330) and a ".CAA" header (0x350).
public static class GbaFooter
{
    public const int MAX_ROM_SIZE = 32 * 1024 * 1024;
    public const int FOOTER_SIZE = 0x360;
    public const int METADATA_SIZE = 0x324;
    private const int _MIN_ROM_SIZE = 0xC0;
    private const int _LUT_OFFSET = 0x24;
    private const int _DESCRIPTORS_OFFSET = 0x330;
    private const int _CAA_OFFSET = 0x350;

    // Emulated save chip timings at 0x10: flash chip erase, flash sector erase, flash program and
    // EEPROM write cycles (libn3ds lgy9.c).
    private static readonly uint[] _TIMING_512K_4K = [0x27C886, 0x8CE35, 0x184, 0x31170];
    private static readonly uint[] _TIMING_1M_64K = [0x17D43E, 0x26206, 0x86, 0x2DD13];

    // Screen presets: "vc" is the official 70% dim filter, "corrected" a gamma correction.
    private static readonly Dictionary<string, (double Brightness, bool Corrected)> _SCREENS = new()
    {
        ["bright"] = (1.0, false), ["vc"] = (0.7, false), ["corrected"] = (1.0, true),
    };

    // LCD ghosting: 0xFF is none, lower values blend more of the previous frame.
    private static readonly Dictionary<string, uint> _GHOSTING = new()
    {
        ["off"] = 0xFF, ["light"] = 0xC0, ["default"] = 0x80, ["strong"] = 0x40,
    };

    public static bool HeaderValid(ReadOnlySpan<byte> rom)
    {
        var sum = 0;
        foreach (var value in rom[0xA0..0xBD]) sum += value;
        return rom[0xB2] == 0x96 && rom[0xBD] == ((-sum - 0x19) & 0xFF);
    }

    internal static void CheckRomSize(long length)
    {
        if (length < _MIN_ROM_SIZE)
            throw new InvalidDataException("File is too small to be a GBA ROM.");
        if (length > MAX_ROM_SIZE)
            throw new InvalidDataException("GBA ROMs can be at most 32 MiB.");
    }

    public static byte[] BuildCode(byte[] rom, SaveType saveType, string screen, string ghosting)
    {
        CheckRomSize(rom.Length);
        if (!_SCREENS.TryGetValue(screen, out var preset))
            throw new ArgumentException($"Unknown GBA screen: {screen}");
        if (!_GHOSTING.TryGetValue(ghosting, out var blend))
            throw new ArgumentException($"Unknown GBA ghosting: {ghosting}");

        var romSize = (rom.Length + 0xF) & ~0xF;
        var code = new byte[romSize + FOOTER_SIZE];
        rom.CopyTo(code, 0);
        var footer = code.AsSpan(romSize);
        var timing = saveType is SaveType.EEPROM_64K or SaveType.EEPROM_64K_LARGE_ROM
            or SaveType.FLASH_1M_MACRONIX_RTC or SaveType.FLASH_1M_MACRONIX
            or SaveType.FLASH_1M_SANYO_RTC or SaveType.FLASH_1M_SANYO
            ? _TIMING_1M_64K : _TIMING_512K_4K;
        WriteWords(footer, 0x00, 0, (uint)romSize, (uint)saveType, 0xFFFF);
        WriteWords(footer, 0x10, timing);
        WriteWords(footer, 0x20, blend);
        WriteVideoLut(footer[_LUT_OFFSET..METADATA_SIZE], preset.Brightness, preset.Corrected);
        WriteWords(footer, _DESCRIPTORS_OFFSET, 0, 0, (uint)romSize, 0);
        WriteWords(footer, _DESCRIPTORS_OFFSET + 0x10, 1, (uint)romSize, METADATA_SIZE, 0);
        ".CAA"u8.CopyTo(footer[_CAA_OFFSET..]);
        WriteWords(footer, _CAA_OFFSET + 4, 1, (uint)(romSize + _DESCRIPTORS_OFFSET), 2 << 4);
        return code;
    }

    // RGB-interleaved output LUT. Plain presets scale by floor(i * brightness) like official
    // titles; "corrected" maps input gamma 2.2 to output gamma 1.54 (agb_edit's default).
    private static void WriteVideoLut(Span<byte> lut, double brightness, bool corrected)
    {
        var scale = (int)Math.Round(brightness * 1000, MidpointRounding.ToEven);
        for (var i = 0; i < 256; i++)
        {
            var value = corrected
                ? (byte)Math.Round(255 * brightness * Math.Pow(i / 255.0, 2.2 / 1.54),
                    MidpointRounding.ToEven)
                : (byte)(i * scale / 1000);
            lut.Slice(i * 3, 3).Fill(value);
        }
    }

    private static void WriteWords(Span<byte> target, int offset, params ReadOnlySpan<uint> words)
    {
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(target[(offset + i * 4)..], words[i]);
    }
}
