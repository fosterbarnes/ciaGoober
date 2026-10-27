using System.Buffers.Binary;
using CiaGoober.Core;

namespace CiaGoober.Tests;

// The data.bin header is what the New 3DS SNES VC emulator reads (3dbrew "3DS Virtual Console");
// these bytes and the chip gate must not drift.
public sealed class SnesTests
{
    // A 512 KiB ROM with a valid header at the LoROM or HiROM spot; byte i holds i's low byte
    // elsewhere.
    private static byte[] Rom(bool hiRom, byte map, byte type, byte sram = 0, byte subtype = 0)
    {
        var rom = new byte[0x80000];
        for (var i = 0; i < rom.Length; i++) rom[i] = (byte)i;
        var at = hiRom ? 0xFFC0 : 0x7FC0;
        var header = rom.AsSpan(at, 0x40);
        "TEST GAME            "u8.CopyTo(header);
        header[0x15] = map;
        header[0x16] = type;
        header[0x18] = sram;
        header[0x1A] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x1C..], 0x1234 ^ 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x1E..], 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x3C..], 0x8000);
        rom[at - 1] = subtype;
        return rom;
    }

    private static byte[] DonorHeader()
    {
        var header = new byte[SnesRom.DATA_HEADER_SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x100);
        "KTR-UAAE"u8.CopyTo(header.AsSpan(0x24));
        header[0x30] = 0x3C;
        header[0x3F] = 0x02;
        header[0x40] = 0x80;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x3D), 0x1011);
        return header;
    }

    [Theory]
    [InlineData(false, 0x20, 0x14)]
    [InlineData(true, 0x31, 0x15)]
    public void BuildsDataBinWithoutCopierHeader(bool hiRom, byte map, byte romType)
    {
        var rom = Rom(hiRom, map, 0x02, sram: 3);
        var image = SnesRom.Parse([.. new byte[0x200], .. rom]);
        Assert.Equal(rom, image.Rom);
        Assert.Equal(hiRom, image.HiRom);
        Assert.Equal(0x2000, image.SramSize);

        var data = SnesRom.ToDataBin(image, DonorHeader(), 0x0000);
        var size = (uint)(0x60 + rom.Length);
        uint Word(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        Assert.Equal(new[] { 0x100u, size, 0x60u, size, size, size },
            new[] { Word(0x00), Word(0x04), Word(0x10), Word(0x14), Word(0x18), Word(0x20) });
        Assert.Equal("KTR-UAAE"u8.ToArray(), data[0x24..0x2C]);
        Assert.Equal(new byte[] { 0x3C, 0x00, 0x00, 0x08 }, data[0x30..0x34]);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02, 0x80, romType },
            data[0x35..0x42]);
        Assert.Equal(rom, data[0x60..]);
    }

    [Theory]
    [InlineData(false, 0x20, 0x15, 0, "SuperFX")]
    [InlineData(false, 0x23, 0x35, 0, "SA-1")]
    [InlineData(true, 0x3A, 0xF9, 0, "SPC7110")]
    [InlineData(false, 0x20, 0xF5, 0x01, "ST010")]
    public void RejectsChipsTheEmulatorLacks(bool hiRom, byte map, byte type, byte subtype,
        string chip)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            SnesRom.Parse(Rom(hiRom, map, type, subtype: subtype)));
        Assert.Contains(chip, error.Message);
    }

    // A chosen preset wins; a plain cart gets the generic preset; a chip game without a preset
    // stops the build instead of shipping a game that cannot run.
    [Fact]
    public void PicksPresetOrRefuses()
    {
        var cx4 = SnesRom.Parse(Rom(false, 0x20, 0xF3, subtype: 0x10)).Chip;
        var dsp1 = SnesRom.Parse(Rom(false, 0x20, 0x03)).Chip;
        Assert.Equal((SnesChip.Cx4, SnesChip.Dsp1), (cx4, dsp1));
        Assert.Equal(0x1117, VcForwarderBuilder.SnesPreset(cx4, 0x1117));
        Assert.Equal(SnesRom.GENERIC_PRESET, VcForwarderBuilder.SnesPreset(SnesChip.None, null));
        Assert.Throws<InvalidOperationException>(() => VcForwarderBuilder.SnesPreset(dsp1, null));
    }
}
