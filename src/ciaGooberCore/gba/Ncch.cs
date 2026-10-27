using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CiaGoober.Core;

internal sealed record NcchParts(ulong TitleId, string ProductCode, byte[] Exheader,
    Dictionary<string, byte[]> ExefsFiles, byte[] LogoRegion, byte[] RomFs);

// Unencrypted NCCH (CXI) and ExeFS packing, and the parsing needed to pull the template out of a
// decrypted donor title (3dbrew NCCH, ExeFS, CIA).
internal static class Ncch
{
    public const int MEDIA_UNIT = 0x200;
    public const int EXHEADER_SIZE = 0x400;
    public const int EXHEADER_FULL_SIZE = 0x800;
    private const int _ROMFS_ALIGN = 0x1000;
    private const byte _PLATFORM_CTR = 0x01;
    private const byte _CONTENT_EXECUTABLE_DATA = 0x03;
    private const byte _FLAG_NO_CRYPTO = 0x04;

    // A 0x200-byte header of (name, offset, size) entries and SHA-256 hashes, then 0x200-aligned
    // files. Hashes are stored in reverse order: the first file's hash comes last.
    public static byte[] BuildExefs(params (string Name, byte[] Data)[] files)
    {
        if (files.Length > 10) throw new ArgumentException("ExeFS can hold at most 10 files.");
        using var exefs = new MemoryStream();
        var header = new byte[MEDIA_UNIT];
        exefs.Write(header);
        for (var i = 0; i < files.Length; i++)
        {
            var (name, data) = files[i];
            var rawName = Encoding.ASCII.GetBytes(name);
            if (rawName.Length > 8)
                throw new ArgumentException($"ExeFS file name too long: {name}");
            rawName.CopyTo(header, i * 0x10);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(i * 0x10 + 8),
                (uint)(exefs.Length - MEDIA_UNIT));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(i * 0x10 + 12), (uint)data.Length);
            SHA256.HashData(data, header.AsSpan(0xC0 + (9 - i) * 0x20));
            exefs.Write(data);
            exefs.Write(new byte[Pad(data.Length, MEDIA_UNIT)]);
        }
        exefs.Position = 0;
        exefs.Write(header);
        return exefs.ToArray();
    }

    // Changes only the system control info and ARM11 local caps; the access descriptor
    // (0x400-0x800) is signed and stays untouched.
    public static byte[] PatchExheader(byte[] exheader, ulong titleId, string appTitle,
        ulong saveDataSize)
    {
        if (exheader.Length != EXHEADER_FULL_SIZE)
            throw new InvalidDataException("Template exheader must be 0x800 bytes.");
        var patched = (byte[])exheader.Clone();
        var title = Encoding.ASCII.GetBytes(appTitle);
        patched.AsSpan(0, 8).Clear();
        title.AsSpan(0, Math.Min(title.Length, 8)).CopyTo(patched);
        // Save data size also ends up in the TMD; then jump ID and program ID.
        BinaryPrimitives.WriteUInt64LittleEndian(patched.AsSpan(0x1C0), saveDataSize);
        BinaryPrimitives.WriteUInt64LittleEndian(patched.AsSpan(0x1C8), titleId);
        BinaryPrimitives.WriteUInt64LittleEndian(patched.AsSpan(0x200), titleId);
        return patched;
    }

    // Unencrypted CXI with every header hash filled in.
    public static byte[] BuildNcch(ulong titleId, string productCode, byte[] exheader, byte[] exefs,
        byte[] romFs)
    {
        if (!Ascii.IsValid(productCode) || productCode.Length > 0x10)
            throw new ArgumentException("Product code must be at most 16 characters.");
        const int exefsOffset = MEDIA_UNIT + EXHEADER_FULL_SIZE;
        var exefsSize = exefs.Length + Pad(exefs.Length, MEDIA_UNIT);
        var romFsOffset = romFs.Length > 0 ? Align(exefsOffset + exefsSize, _ROMFS_ALIGN) : 0;
        var romFsSize = romFs.Length + Pad(romFs.Length, MEDIA_UNIT);
        var total = romFs.Length > 0 ? romFsOffset + romFsSize : exefsOffset + exefsSize;

        var ncch = new byte[total];
        var header = ncch.AsSpan(0, MEDIA_UNIT);
        "NCCH"u8.CopyTo(header[0x100..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x104..], (uint)(total / MEDIA_UNIT));
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x108..], titleId);
        "01"u8.CopyTo(header[0x110..]);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x112..], 2);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x118..], titleId);
        Encoding.ASCII.GetBytes(productCode).CopyTo(header[0x150..]);
        SHA256.HashData(exheader.AsSpan(0, EXHEADER_SIZE), header[0x160..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x180..], EXHEADER_SIZE);
        header[0x18C] = _PLATFORM_CTR;
        header[0x18D] = _CONTENT_EXECUTABLE_DATA;
        header[0x18F] = _FLAG_NO_CRYPTO;
        WriteWords(header[0x1A0..], exefsOffset / MEDIA_UNIT, exefsSize / MEDIA_UNIT, 1);
        exheader.CopyTo(ncch, MEDIA_UNIT);
        exefs.CopyTo(ncch, exefsOffset);
        SHA256.HashData(ncch.AsSpan(exefsOffset, MEDIA_UNIT), header[0x1C0..]);
        if (romFs.Length > 0)
        {
            romFs.CopyTo(ncch, romFsOffset);
            // The header hash covers the IVFC header and master hash.
            var masterSize = romFs.AsSpan().StartsWith("IVFC"u8)
                ? BinaryPrimitives.ReadUInt32LittleEndian(romFs.AsSpan(0x08)) : 0;
            var hashRegion = Align(0x60L + masterSize, MEDIA_UNIT);
            WriteWords(header[0x1B0..], romFsOffset / MEDIA_UNIT, romFsSize / MEDIA_UNIT,
                (int)(hashRegion / MEDIA_UNIT));
            SHA256.HashData(ncch.AsSpan(romFsOffset, (int)Math.Min(hashRegion, romFsSize)),
                header[0x1E0..]);
        }
        return ncch;
    }

    public static NcchParts Parse(byte[] data)
    {
        if (data.Length < MEDIA_UNIT || !data.AsSpan(0x100, 4).SequenceEqual("NCCH"u8))
            throw new InvalidDataException("Not an NCCH file (or it is still encrypted).");
        if ((data[0x18F] & _FLAG_NO_CRYPTO) == 0)
            throw new InvalidDataException(
                "This NCCH is encrypted. Decrypt it first (e.g. with GodMode9).");

        byte[] Region(int field)
        {
            long offset = ReadUInt32(data, field), size = ReadUInt32(data, field + 4);
            return Slice(data, offset * MEDIA_UNIT, (offset + size) * MEDIA_UNIT);
        }

        var exheader = ReadUInt32(data, 0x180) != 0
            ? Slice(data, MEDIA_UNIT, MEDIA_UNIT + EXHEADER_FULL_SIZE) : [];
        var exefs = Region(0x1A0);
        var files = new Dictionary<string, byte[]>();
        for (var i = 0; i < 10; i++)
        {
            var name = AsciiText(exefs.AsSpan(i * 0x10, 8).TrimEnd((byte)0));
            long offset = ReadUInt32(exefs, i * 0x10 + 8), size = ReadUInt32(exefs, i * 0x10 + 12);
            if (name.Length > 0) files[name] = Slice(exefs, 0x200 + offset, 0x200 + offset + size);
        }
        return new NcchParts(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x118)),
            AsciiText(data.AsSpan(0x150, 0x10).TrimEnd((byte)0)), exheader, files, Region(0x198),
            Region(0x1B0));
    }

    // A decrypted donor title: a .cia, or a .cxi/.app that is the NCCH itself.
    public static NcchParts ParseTitle(string path)
    {
        var data = File.ReadAllBytes(path);
        try
        {
            var isNcch = data.Length >= 0x104 && data.AsSpan(0x100, 4).SequenceEqual("NCCH"u8);
            return Parse(isNcch ? data : FirstCiaContent(data));
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException($"{error.Message} Rebuild it in GodMode9: Title manager "
                + "-> the game -> Build CIA (standard).", error);
        }
    }

    // The first content (the CXI) of a CIA whose contents are not encrypted.
    public static byte[] FirstCiaContent(byte[] data)
    {
        if (ReadUInt32(data, 0) != 0x2020) throw new InvalidDataException("Not a CIA file.");
        var offset = 0L;
        foreach (var field in (int[])[0, 8, 12, 16]) offset += Align(ReadUInt32(data, field), 64);
        var contentSize = Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x18)),
            (ulong)data.Length);
        var content = Slice(data, offset, offset + (long)contentSize);
        if (!Slice(content, 0x100, 0x104).AsSpan().SequenceEqual("NCCH"u8))
            throw new InvalidDataException("The CIA content is encrypted. Use a decrypted CIA "
                + "(GodMode9: 'Build CIA (decrypted)' or 'Decrypt file').");
        return Slice(content, 0, ReadUInt32(content, 0x104) * MEDIA_UNIT);
    }

    // Python slice semantics for non-negative bounds: out-of-range ends are clamped.
    private static byte[] Slice(byte[] data, long start, long end)
    {
        start = Math.Min(start, data.Length);
        return data.AsSpan((int)start, (int)(Math.Clamp(end, start, data.Length) - start)).ToArray();
    }

    // Python's ascii decode with errors="replace".
    private static string AsciiText(ReadOnlySpan<byte> raw)
    {
        var chars = new char[raw.Length];
        for (var i = 0; i < raw.Length; i++) chars[i] = raw[i] < 0x80 ? (char)raw[i] : '�';
        return new string(chars);
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

    private static void WriteWords(Span<byte> target, params ReadOnlySpan<int> words)
    {
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(target[(i * 4)..], (uint)words[i]);
    }

    private static int Pad(int length, int alignment) => (alignment - length % alignment) % alignment;
    private static int Align(int value, int alignment) => value + Pad(value, alignment);
    private static long Align(long value, long alignment) =>
        (value + alignment - 1) / alignment * alignment;
}
