using System.Buffers.Binary;
using CiaGoober.Core;

namespace CiaGoober.Tests;

public sealed class CiaReaderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ciaGooberTests").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void ReadsForwarderTitleId() =>
        Assert.Equal(0xFF4CC, CiaReader.UniqueId(WriteCia([0, 4, 0, 0, 0x0F, 0xF4, 0xCC, 0])));

    [Theory]
    [InlineData(new byte[] { 0, 4, 0, 0x80, 0x0F, 0xF4, 0xCC, 0 })]
    [InlineData(new byte[] { 0, 4, 0, 0, 0x0F, 0xF4, 0xCC, 2 })]
    public void RejectsOtherTitles(byte[] titleId) =>
        Assert.Throws<InvalidDataException>(() => CiaReader.UniqueId(WriteCia(titleId)));

    [Fact]
    public void RejectsUnknownSignature() =>
        Assert.Throws<InvalidDataException>(() =>
            CiaReader.UniqueId(WriteCia([0, 4, 0, 0, 0x0F, 0xF4, 0xCC, 0], 0x10006)));

    // Header (0x20, padded to 0x40), no cert or ticket, then a TMD with a 0x140-byte signature.
    private string WriteCia(byte[] titleId, uint signatureType = 0x10004)
    {
        const int tmd = 0x40, signature = 0x140;
        var cia = new byte[tmd + signature + 0x54];
        BinaryPrimitives.WriteUInt32LittleEndian(cia, 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(cia.AsSpan(16), signature + 0x54);
        BinaryPrimitives.WriteUInt32BigEndian(cia.AsSpan(tmd), signatureType);
        titleId.CopyTo(cia, tmd + signature + 0x4C);
        var path = Path.Combine(_folder, $"{Guid.NewGuid():N}.cia");
        File.WriteAllBytes(path, cia);
        return path;
    }
}
