using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace CiaGoober.Core;

// Expansion chips the New 3DS SNES VC emulator runs (3dbrew "3DS Virtual Console").
public enum SnesChip { None, Dsp1, Dsp2, Cx4 }

// A ROM without its copier header, as data.bin carries it. SramSize is the cartridge RAM the
// header declares, in bytes.
public sealed record SnesImage(byte[] Rom, bool HiRom, SnesChip Chip, int SramSize);

// A VC title's data.bin header and the chip of its game, whose preset ID runs it.
public sealed record SnesDonor(byte[] Header, SnesChip Chip)
{
    public ushort Preset => BinaryPrimitives.ReadUInt16LittleEndian(Header.AsSpan(0x3D));
}

// SNES cartridge header (SNESdev "SNES ROM Header") and the data.bin the New 3DS SNES VC emulator
// reads (3dbrew "3DS Virtual Console"): a 0x60 header, the ROM, then optional PCM audio, S-DD1
// data and footer, which injected games leave out.
public static class SnesRom
{
    public const int DATA_HEADER_SIZE = 0x60;
    // Plain carts; the emulator's preset for "no special settings" (SNES Classic research).
    public const ushort GENERIC_PRESET = 0x0000;
    private const int _COPIER_HEADER = 0x200;
    // 48 Mbit, the largest ROM the Ultimate SNES VC Injector ran.
    private const int _MAX_ROM_SIZE = 6 * 1024 * 1024;
    private const int _LOROM_HEADER = 0x7FC0;
    private const int _HIROM_HEADER = 0xFFC0;
    private const int _EXHIROM_HEADER = 0x40FFC0;
    private const int _TITLE_SIZE = 21;
    private const byte _LOROM_TYPE = 0x14;
    private const byte _HIROM_TYPE = 0x15;
    private const string _SYSTEM_NAME = "Super Nintendo Entertainment System";

    // Map mode low nibbles a header at each spot may carry, and the unsupported ones by name.
    private static readonly HashSet<int> _LOROM_MAPS = [0x0, 0x2, 0x3];
    private static readonly HashSet<int> _HIROM_MAPS = [0x1, 0x5, 0xA];
    private static readonly Dictionary<int, string> _UNSUPPORTED_MAPS = new()
    {
        [0x2] = "S-DD1", [0x3] = "SA-1", [0x5] = "ExHiROM", [0xA] = "SPC7110",
    };
    // Cartridge type high nibble -> coprocessor the emulator lacks; 0 (DSP) and F (custom) are
    // told apart below.
    private static readonly Dictionary<int, string> _UNSUPPORTED_CHIPS = new()
    {
        [0x1] = "SuperFX", [0x2] = "OBC1", [0x3] = "SA-1", [0x4] = "S-DD1", [0x5] = "S-RTC",
        [0xE] = "Super Game Boy or Satellaview",
    };

    // Throws InvalidDataException for a ROM the SNES VC emulator cannot run.
    public static RomInfo Read(string path)
    {
        if (new FileInfo(path).Length > _MAX_ROM_SIZE + _COPIER_HEADER)
            throw new InvalidDataException("SNES ROMs can be at most 6 MiB (48 Mbit).");
        var image = Parse(File.ReadAllBytes(path));
        var header = image.Rom.AsSpan(image.HiRom ? _HIROM_HEADER : _LOROM_HEADER);
        var title = HeaderText(header[.._TITLE_SIZE]);
        var name = RomNames.FromFileName(path, title, _SYSTEM_NAME);
        return new RomInfo(path, GameSystem.Snes, GameCode(image.Rom, image.HiRom),
            [name, name, _SYSTEM_NAME], title) { SnesChip = image.Chip };
    }

    public static SnesImage Parse(byte[] file)
    {
        var rom = file.Length % 0x400 == _COPIER_HEADER ? file[_COPIER_HEADER..] : file;
        if (rom.Length > _MAX_ROM_SIZE)
            throw new InvalidDataException("SNES ROMs can be at most 6 MiB (48 Mbit).");
        var lo = Score(rom, _LOROM_HEADER, _LOROM_MAPS);
        var hi = Score(rom, _HIROM_HEADER, _HIROM_MAPS);
        if (Score(rom, _EXHIROM_HEADER, _HIROM_MAPS) > Math.Max(lo, hi))
            throw new InvalidDataException("ExHiROM games are not supported by the SNES Virtual "
                + "Console emulator.");
        if (Math.Max(lo, hi) < 3)
            throw new InvalidDataException("No SNES ROM header found (corrupt or interleaved ROM).");
        var hiRom = hi > lo;
        var at = hiRom ? _HIROM_HEADER : _LOROM_HEADER;
        var map = rom[at + 0x15] & 0x0F;
        if (_UNSUPPORTED_MAPS.TryGetValue(map, out var mapName))
            throw Unsupported(mapName);
        if (map != (hiRom ? 1 : 0))
            throw new InvalidDataException("The ROM header's map mode does not match its layout.");
        var type = rom[at + 0x16];
        var hasRam = (type & 0x0F) is 1 or 2 or 4 or 5;
        var sramCode = rom[at + 0x18];
        if (hasRam && sramCode > 8)
            throw new InvalidDataException($"Invalid SRAM size in the ROM header ({sramCode}).");
        return new SnesImage(rom, hiRom, Chip(rom, at),
            hasRam && sramCode > 0 ? 0x400 << sramCode : 0);
    }

    // The data.bin of one game: the donor's header with this ROM's layout, size, type and preset.
    public static byte[] ToDataBin(SnesImage image, byte[] donorHeader, ushort preset)
    {
        var data = new byte[DATA_HEADER_SIZE + image.Rom.Length];
        donorHeader.AsSpan(0, DATA_HEADER_SIZE).CopyTo(data);
        var span = data.AsSpan();
        var size = (uint)data.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x04..], size);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x10..], DATA_HEADER_SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x14..], size);
        // No PCM footer and no S-DD1 data: both start at the end of the file.
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x18..], size);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x20..], size);
        WriteUInt24(span[0x31..], image.Rom.Length);
        WriteUInt24(span[0x35..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x39..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x3D..], preset);
        data[0x41] = image.HiRom ? _HIROM_TYPE : _LOROM_TYPE;
        image.Rom.CopyTo(data, DATA_HEADER_SIZE);
        return data;
    }

    // The donor title's data.bin header and the chip of its game; throws when it is not one.
    public static SnesDonor ReadDonor(byte[] data)
    {
        if (data.Length < DATA_HEADER_SIZE
            || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x100
            || data[0x41] is not (_LOROM_TYPE or _HIROM_TYPE))
            throw new InvalidDataException("The donor's data.bin is not an SNES Virtual Console "
                + "game.");
        var start = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x10));
        var end = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x14));
        if (start < DATA_HEADER_SIZE || end <= start || end > data.Length)
            throw new InvalidDataException("The donor's data.bin has an invalid ROM range.");
        var chip = Parse(data[(int)start..(int)end]).Chip;
        return new SnesDonor(data[..DATA_HEADER_SIZE], chip);
    }

    // Preset IDs of official New 3DS SNES VC titles with the chip, read from their data.bin:
    // Super Mario Kart (KTR-UADE) and Mega Man X2 (KTR-UATE). No DSP-2 title was released.
    public static readonly IReadOnlyDictionary<SnesChip, ushort> KNOWN_PRESETS =
        new Dictionary<SnesChip, ushort> { [SnesChip.Dsp1] = 0x10BD, [SnesChip.Cx4] = 0x1117 };

    // "1011" or "0x1011"; empty text is no preset. False for anything else.
    public static bool TryParsePreset(string text, out ushort? preset)
    {
        preset = null;
        text = text.Trim();
        if (text.Length == 0) return true;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (!ushort.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
            out var value)) return false;
        preset = value;
        return true;
    }

    // Chip name with a game that uses it.
    public static string ChipLabel(SnesChip chip) => chip switch
    {
        SnesChip.Dsp1 => "DSP-1 (Super Mario Kart)",
        SnesChip.Dsp2 => "DSP-2 (Dungeon Master)",
        SnesChip.Cx4 => "Cx4 (Mega Man X2)",
        _ => ChipName(chip),
    };

    public static string ChipName(SnesChip chip) => chip switch
    {
        SnesChip.None => "None",
        SnesChip.Dsp1 => "DSP-1",
        SnesChip.Dsp2 => "DSP-2",
        SnesChip.Cx4 => "Cx4",
        _ => throw new ArgumentOutOfRangeException(nameof(chip), chip, null),
    };

    // Checksum pair, a map mode that fits the spot, a reset vector in ROM and a text title; the
    // usual LoROM/HiROM test (SNESdev "ROM header").
    private static int Score(byte[] rom, int at, HashSet<int> maps)
    {
        if (rom.Length < at + 0x40) return -1;
        var header = rom.AsSpan(at, 0x40);
        var score = 0;
        if ((header[0x15] & 0xE0) == 0x20 && maps.Contains(header[0x15] & 0x0F)) score += 2;
        if ((BinaryPrimitives.ReadUInt16LittleEndian(header[0x1C..])
            ^ BinaryPrimitives.ReadUInt16LittleEndian(header[0x1E..])) == 0xFFFF) score += 4;
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[0x3C..]) >= 0x8000) score += 1;
        if (IsTitleText(header[.._TITLE_SIZE])) score += 1;
        return score;
    }

    // ASCII or JIS X 0201 katakana.
    private static bool IsTitleText(ReadOnlySpan<byte> title)
    {
        foreach (var value in title)
            if (value is not (>= 0x20 and <= 0x7E or >= 0xA0 and <= 0xDF)) return false;
        return true;
    }

    private static SnesChip Chip(byte[] rom, int at)
    {
        var type = rom[at + 0x16];
        if ((type & 0x0F) < 3) return SnesChip.None;
        var coprocessor = type >> 4;
        if (_UNSUPPORTED_CHIPS.TryGetValue(coprocessor, out var name)) throw Unsupported(name);
        switch (coprocessor)
        {
            case 0x0:
                // DSP-1 to DSP-4 share one type; bsnes tells them apart by title.
                var title = rom.AsSpan(at, _TITLE_SIZE);
                if (title.StartsWith("DUNGEON MASTER"u8)) return SnesChip.Dsp2;
                if (title.StartsWith("TOP GEAR 3000"u8)) throw Unsupported("DSP-4");
                if (title.StartsWith("SD"u8) && title.IndexOf("GX"u8) > 0) throw Unsupported("DSP-3");
                return SnesChip.Dsp1;
            case 0xF:
                return rom[at - 1] switch
                {
                    0x10 => SnesChip.Cx4,
                    0x00 => throw Unsupported("SPC7110"),
                    0x01 => throw Unsupported("ST010/ST011"),
                    0x02 => throw Unsupported("ST018"),
                    var subtype => throw Unsupported($"custom chip 0x{subtype:X2}"),
                };
            default:
                throw Unsupported($"coprocessor 0x{coprocessor:X}");
        }
    }

    // The 4-character code of the extended header (0xFFB2), else "SNSH".
    private static string GameCode(byte[] rom, bool hiRom)
    {
        var at = hiRom ? _HIROM_HEADER : _LOROM_HEADER;
        if (rom[at + 0x1A] != 0x33) return "SNSH";
        var code = rom.AsSpan(at - 0x0E, 4);
        foreach (var value in code)
            if (!char.IsAsciiLetterUpper((char)value) && !char.IsAsciiDigit((char)value))
                return "SNSH";
        return Encoding.ASCII.GetString(code);
    }

    // Cut at the first NUL; non-ASCII bytes become U+FFFD.
    private static string HeaderText(ReadOnlySpan<byte> raw)
    {
        var end = raw.IndexOf((byte)0);
        if (end >= 0) raw = raw[..end];
        var chars = new char[raw.Length];
        for (var i = 0; i < raw.Length; i++)
            chars[i] = raw[i] is >= 0x20 and < 0x7F ? (char)raw[i] : '�';
        return new string(chars).Trim();
    }

    private static void WriteUInt24(Span<byte> target, int value)
    {
        target[0] = (byte)value;
        target[1] = (byte)(value >> 8);
        target[2] = (byte)(value >> 16);
    }

    private static InvalidDataException Unsupported(string what) =>
        new($"{what} games are not supported by the SNES Virtual Console emulator.");
}
