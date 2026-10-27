using System.Text.RegularExpressions;

namespace CiaGoober.Core;

// Game Boy and Game Boy Color cartridge header (0x100-0x14F, Pan Docs "The Cartridge Header").
public static partial class GbRom
{
    public const int MIN_ROM_SIZE = 0x150;
    public const int MAX_ROM_SIZE = 8 * 1024 * 1024;
    private const int _CGB_FLAG = 0x143;
    private const int _CART_TYPE = 0x147;
    private const int _RAM_SIZE = 0x149;
    private const int _HEADER_CHECKSUM = 0x14D;
    // MBC2 carts report RAM code 0 but hold 512 half-bytes.
    private const int _MBC2_RAM = 0x200;

    // Cartridge RAM size code -> bytes.
    private static readonly int[] _RAM_BYTES = [0, 0x800, 0x2000, 0x8000, 0x20000, 0x10000];

    public static RomInfo Read(string path)
    {
        var header = new byte[MIN_ROM_SIZE];
        using (var stream = File.OpenRead(path))
        {
            if (stream.ReadAtLeast(header, MIN_ROM_SIZE, throwOnEndOfStream: false) < MIN_ROM_SIZE)
                throw new InvalidDataException($"File is too small to be a Game Boy ROM: {path}");
        }
        var color = IsColor(header);
        // Color carts use the last title bytes for the CGB flag and, on later carts, a game code.
        var code = HeaderText(header.AsSpan(0x13F, 4));
        var hasCode = color && CodeChars().IsMatch(code);
        var title = HeaderText(header.AsSpan(0x134, hasCode ? 11 : color ? 15 : 16));
        var system = color ? "Game Boy Color" : "Game Boy";
        var name = RomNames.FromFileName(path, title, system);
        return new RomInfo(path, GameSystem.Gb, hasCode ? code : color ? "CGBH" : "DMGH",
            [name, name, system], title);
    }

    // CGB flag 0x80 (also runs on a Game Boy) or 0xC0 (Color only).
    public static bool IsColor(ReadOnlySpan<byte> rom) =>
        rom.Length > _CGB_FLAG && (rom[_CGB_FLAG] & 0x80) != 0;

    public static bool HeaderValid(ReadOnlySpan<byte> rom)
    {
        var sum = 0;
        foreach (var value in rom[0x134.._HEADER_CHECKSUM]) sum = sum - value - 1;
        return rom[_HEADER_CHECKSUM] == (byte)sum;
    }

    // Battery or work RAM the cartridge declares, in bytes.
    public static int RamSize(ReadOnlySpan<byte> rom) =>
        rom[_CART_TYPE] is 0x05 or 0x06 ? _MBC2_RAM
        : rom[_RAM_SIZE] < _RAM_BYTES.Length ? _RAM_BYTES[rom[_RAM_SIZE]] : 0;

    internal static void CheckRomSize(long length)
    {
        if (length < MIN_ROM_SIZE)
            throw new InvalidDataException("File is too small to be a Game Boy ROM.");
        if (length > MAX_ROM_SIZE)
            throw new InvalidDataException("Game Boy ROMs can be at most 8 MiB.");
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

    [GeneratedRegex("^[A-Z0-9]{4}$")]
    private static partial Regex CodeChars();
}
