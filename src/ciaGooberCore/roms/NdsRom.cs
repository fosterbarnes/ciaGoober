using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CiaGoober.Core;

public static class NdsRom
{
    private const int _HEADER_SIZE = 0x6C;
    private const int _BANNER_V1_SIZE = 0x840;
    private const int _BANNER_MAX_SIZE = 0xA40;
    private const int _TITLE_OFFSET = 0x240;
    private const int _TITLE_SIZE = 0x100;
    // Banner title slots are stored jpn, eng, fra, ger, ita, spa, chn, kor; keys are listed in
    // makesmdh order (generator.py:269-280) so the English fallback order is the dictionary order.
    private static readonly (string Key, int Slot)[] _LANGUAGES =
    [
        ("eng", 1), ("jpn", 0), ("fra", 2), ("ger", 3), ("ita", 4), ("spa", 5), ("chn", 6),
        ("kor", 7),
    ];

    public static RomInfo Read(string path)
    {
        using var file = File.OpenHandle(path);
        var header = ReadExact(file, 0, _HEADER_SIZE, path, "header");
        var internalTitle = Encoding.ASCII.GetString(header, 0, 12).Trim('\0', ' ');
        var codeBytes = header.AsSpan(0x0C, 4);
        if (codeBytes.ContainsAnyExceptInRange((byte)0, (byte)0x7F))
            throw new InvalidDataException($"NDS game code is not ASCII: {path}");
        var gameCode = Encoding.ASCII.GetString(codeBytes);
        var lines = Titles(file, header, path).Values.FirstOrDefault(value => value is not null);
        string[] titleLines = lines is null
            ? [internalTitle, internalTitle, ""]
            : [string.Join(' ', lines[..^1]), string.Join(' ', lines[..^1]), lines[^1]];
        return new RomInfo(path, GameSystem.Nds, gameCode, titleLines, internalTitle);
    }

    // Banner titles per language present in the banner version; null marks a missing title.
    public static IReadOnlyDictionary<string, string[]?> LanguageTitles(string path)
    {
        using var file = File.OpenHandle(path);
        return Titles(file, ReadExact(file, 0, _HEADER_SIZE, path, "header"), path);
    }

    // The ROM's 32x32 banner icon; palette index 0 has alpha 0.
    public static RgbaImage Icon(string path)
    {
        using var file = File.OpenHandle(path);
        var bannerOffset = BannerOffset(ReadExact(file, 0, _HEADER_SIZE, path, "header"));
        if (bannerOffset == 0) throw new InvalidDataException($"NDS ROM has no banner: {path}");
        // DS icon: 4x4 tiles of 8x8 4bpp pixels (low nibble first) at 0x20, BGR555 palette at 0x220.
        var icon = ReadExact(file, bannerOffset + 0x20, 0x220, path, "banner icon");
        var palette = new byte[16 * 4];
        for (var i = 0; i < 16; i++)
        {
            var color = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(0x200 + i * 2));
            palette[i * 4] = Expand5(color);
            palette[i * 4 + 1] = Expand5(color >> 5);
            palette[i * 4 + 2] = Expand5(color >> 10);
            palette[i * 4 + 3] = i == 0 ? (byte)0 : (byte)255;
        }
        var pixels = new byte[32 * 32 * 4];
        for (var i = 0; i < 0x200; i++)
        {
            int tile = i >> 5, tileX = tile & 3, tileY = tile >> 2, y = (i >> 2) & 7, x = (i & 3) * 2;
            var index = ((tileY * 8 + y) * 32 + tileX * 8 + x) * 4;
            palette.AsSpan((icon[i] & 0xF) * 4, 4).CopyTo(pixels.AsSpan(index));
            palette.AsSpan((icon[i] >> 4) * 4, 4).CopyTo(pixels.AsSpan(index + 4));
        }
        return new RgbaImage(32, 32, pixels);
    }

    // Port of generator.py:214-267 (get_title).
    private static Dictionary<string, string[]?> Titles(SafeFileHandle file, byte[] header,
        string path)
    {
        var titles = new Dictionary<string, string[]?>();
        var bannerOffset = BannerOffset(header);
        if (bannerOffset == 0) return titles;
        var banner = ReadUpTo(file, bannerOffset, _BANNER_MAX_SIZE);
        if (banner.Length < _BANNER_V1_SIZE)
            throw new InvalidDataException($"NDS banner is truncated: {path}");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(banner) & 3;
        var languageCount = version switch { 2 => 7, 3 => 8, _ => 6 };
        // Version 2 adds Chinese (CRC at 0x4 over 0x20..0x940); version 3 adds Korean (CRC at 0x6
        // over 0x20..0xA40). A failed CRC drops the languages it covers.
        if (version >= 2 && !CrcMatches(banner, 0x4, 0x940)) languageCount = 6;
        else if (version == 3 && !CrcMatches(banner, 0x6, 0xA40)) languageCount = 7;
        foreach (var (key, slot) in _LANGUAGES)
        {
            if (slot >= languageCount) continue;
            var text = Encoding.Unicode.GetString(banner, _TITLE_OFFSET + _TITLE_SIZE * slot,
                _TITLE_SIZE);
            var end = text.IndexOf('\0');
            var lines = (end < 0 ? text : text[..end]).Split('\n');
            titles[key] = lines.Length == 1 ? null : lines;
        }
        return titles;
    }

    private static bool CrcMatches(byte[] banner, int crcOffset, int end) =>
        banner.Length >= end && BinaryPrimitives.ReadUInt16LittleEndian(banner.AsSpan(crcOffset))
            == Crc16.Compute(banner.AsSpan(0x20, end - 0x20));

    private static long BannerOffset(byte[] header) =>
        BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x68));

    // round(c * 255 / 31) as in bannergif.py; c * 255 / 31 is never exactly .5, so no tie rule.
    private static byte Expand5(int color) => (byte)(((color & 0x1F) * 255 + 15) / 31);

    private static byte[] ReadExact(SafeFileHandle file, long offset, int length, string path,
        string part)
    {
        var bytes = ReadUpTo(file, offset, length);
        if (bytes.Length < length) throw new InvalidDataException($"NDS {part} is truncated: {path}");
        return bytes;
    }

    private static byte[] ReadUpTo(SafeFileHandle file, long offset, int length)
    {
        var buffer = new byte[length];
        var total = 0;
        while (total < length)
        {
            var read = RandomAccess.Read(file, buffer.AsSpan(total), offset + total);
            if (read == 0) break;
            total += read;
        }
        return total == length ? buffer : buffer[..total];
    }
}
