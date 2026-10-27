using System.Text.RegularExpressions;

namespace CiaGoober.Core;

public static partial class GbaRom
{
    private const int _HEADER_SIZE = 0xC0;
    private const string _SYSTEM_NAME = "Game Boy Advance";

    public static RomInfo Read(string path)
    {
        var header = new byte[_HEADER_SIZE];
        using (var stream = File.OpenRead(path))
        {
            if (stream.ReadAtLeast(header, _HEADER_SIZE, throwOnEndOfStream: false) < _HEADER_SIZE)
                throw new InvalidDataException($"File is too small to be a GBA ROM: {path}");
        }
        var title = HeaderText(header.AsSpan(0xA0, 12));
        var code = NonCodeChars().Replace(HeaderText(header.AsSpan(0xAC, 4)).ToUpperInvariant(), "");
        var name = RomNames.FromFileName(path, title, _SYSTEM_NAME);
        return new RomInfo(path, GameSystem.Gba, code.Length == 4 ? code : "AGBH",
            [name, name, _SYSTEM_NAME], title);
    }

    // vcinject/gba.py _ascii: cut at the first NUL, non-ASCII bytes become U+FFFD, trim.
    private static string HeaderText(ReadOnlySpan<byte> raw)
    {
        var end = raw.IndexOf((byte)0);
        if (end >= 0) raw = raw[..end];
        var chars = new char[raw.Length];
        for (var i = 0; i < raw.Length; i++) chars[i] = raw[i] < 0x80 ? (char)raw[i] : '�';
        return new string(chars).Trim();
    }

    [GeneratedRegex("[^A-Z0-9]")]
    private static partial Regex NonCodeChars();
}
