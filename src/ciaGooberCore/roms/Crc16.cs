namespace CiaGoober.Core;

// GBATEK swiCRC16 (reflected polynomial 0xA001, initial 0xFFFF), as used by DS banners.
internal static class Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return (ushort)crc;
    }
}
