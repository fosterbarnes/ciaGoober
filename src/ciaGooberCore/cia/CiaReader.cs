using System.Buffers.Binary;

namespace CiaGoober.Core;

public static class CiaReader
{
    // TMD signature type -> signature block size (data + padding).
    private static readonly Dictionary<uint, int> _SIGNATURE_SIZES = new()
    {
        [0x10000] = 0x240, [0x10001] = 0x140, [0x10002] = 0x80,
        [0x10003] = 0x240, [0x10004] = 0x140, [0x10005] = 0x80,
    };

    // The unique ID (bytes 4-6) of the signed TMD's title ID, never a file name or byte search.
    public static int UniqueId(string ciaPath)
    {
        using var file = File.OpenRead(ciaPath);
        Span<byte> header = stackalloc byte[0x20];
        if (file.ReadAtLeast(header, header.Length, false) != header.Length)
            throw new InvalidDataException($"Invalid CIA: {ciaPath}");
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var certSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var ticketSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var tmdSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        var tmdOffset = Align(headerSize) + Align(certSize) + Align(ticketSize);
        Span<byte> signature = stackalloc byte[4];
        file.Position = tmdOffset;
        var signed = file.ReadAtLeast(signature, signature.Length, false) == signature.Length;
        if (headerSize < 0x20 || !signed
            || !_SIGNATURE_SIZES.TryGetValue(BinaryPrimitives.ReadUInt32BigEndian(signature), out var size)
            || tmdSize < size + 0x54)
            throw new InvalidDataException($"Invalid CIA TMD: {ciaPath}");
        Span<byte> titleId = stackalloc byte[8];
        file.Position = tmdOffset + size + 0x4C;
        if (file.ReadAtLeast(titleId, titleId.Length, false) != titleId.Length
            || !titleId[..4].SequenceEqual((ReadOnlySpan<byte>)[0, 4, 0, 0]) || titleId[7] != 0)
            throw new InvalidDataException($"Not a native forwarder CIA: {ciaPath}");
        return titleId[4] << 16 | titleId[5] << 8 | titleId[6];
    }

    private static long Align(uint size) => (size + 63L) & ~63L;
}
