using CiaGoober.Core;

namespace CiaGoober.Tests;

// The TNES header is what the NES VC emulator reads (NESdev "TNES"); these bytes must not drift.
public sealed class NesTests
{
    private const int _BANK = 0x2000;

    // iNES header + PRG (16 KB banks) + CHR (8 KB banks), each bank filled with its own index.
    private static byte[] Ines(int prg16, int chr8, int flags6, int flags7 = 0)
    {
        var rom = new byte[16 + (prg16 * 2 + chr8) * _BANK];
        "NES\u001A"u8.CopyTo(rom);
        rom[4] = (byte)prg16;
        rom[5] = (byte)chr8;
        rom[6] = (byte)flags6;
        rom[7] = (byte)flags7;
        for (var bank = 0; bank < prg16 * 2 + chr8; bank++)
            rom.AsSpan(16 + bank * _BANK, _BANK).Fill((byte)(bank + 1));
        return rom;
    }

    [Theory]
    // MMC1 + battery: TNES mapper 1, mapper-controlled mirroring, WRAM, battery.
    [InlineData(8, 0, 0x12, 0x00, new byte[] { 1, 16, 0, 1, 0, 1 }, 0x2000)]
    // NROM vertical: wired mirroring, no WRAM.
    [InlineData(2, 1, 0x01, 0x00, new byte[] { 0, 4, 1, 0, 2, 0 }, 0)]
    // UxROM horizontal (iNES 2 -> TNES 6).
    [InlineData(8, 0, 0x20, 0x00, new byte[] { 6, 16, 0, 0, 1, 0 }, 0)]
    // MMC3 without battery still gets WRAM (iNES 4 -> TNES 3).
    [InlineData(16, 16, 0x40, 0x00, new byte[] { 3, 32, 16, 1, 0, 0 }, 0x2000)]
    // Jaleco JF-13 (iNES 86 -> TNES 31): upper mapper nibble from byte 7.
    [InlineData(2, 1, 0x60, 0x50, new byte[] { 31, 4, 1, 0, 1, 0 }, 0)]
    public void ConvertsInesToTnes(int prg16, int chr8, int flags6, int flags7, byte[] fields,
        int ramSize)
    {
        var ines = Ines(prg16, chr8, flags6, flags7);
        var (tnes, ram) = NesRom.ToTnes(ines, new Progress<string>());
        Assert.Equal("TNES"u8.ToArray(), tnes[..4]);
        Assert.Equal(fields, tnes[4..10]);
        Assert.All(tnes[10..16], value => Assert.Equal(0, value));
        Assert.Equal(ines[16..], tnes[16..]);
        Assert.Equal(ramSize, ram);
    }

    // Data after CHR ROM (PlayChoice INST-ROM) is not part of the TNES image.
    [Fact]
    public void DropsTrailingData()
    {
        var ines = Ines(1, 1, 0x00);
        var (tnes, _) = NesRom.ToTnes([.. ines, .. new byte[0x2010]], new Progress<string>());
        Assert.Equal(ines[16..], tnes[16..]);
    }

    [Theory]
    [InlineData(0x80, 0x10, "Mapper 24")]
    [InlineData(0x04, 0x00, "trainer")]
    public void RejectsUnsupportedRoms(int flags6, int flags7, string reason)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            NesRom.ToTnes(Ines(2, 1, flags6, flags7), new Progress<string>()));
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void RejectsTruncatedRom()
    {
        var ines = Ines(2, 1, 0x00);
        Assert.Throws<InvalidDataException>(() =>
            NesRom.ToTnes(ines[..^1], new Progress<string>()));
    }
}
