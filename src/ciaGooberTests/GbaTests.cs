using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CiaGoober.Core;

namespace CiaGoober.Tests;

// Expected values come from the nds workspace's vcinject/gbaVc.py run on the same synthetic inputs.
public sealed class GbaTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ciaGooberTests").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    // The 3DS discards a save whose SD file size changes, so these sizes must never drift.
    [Theory]
    [InlineData(SaveType.EEPROM_8K, 0x200, 0x30000)]
    [InlineData(SaveType.EEPROM_8K_LARGE_ROM, 0x200, 0x30000)]
    [InlineData(SaveType.EEPROM_64K, 0x2000, 0x30000)]
    [InlineData(SaveType.EEPROM_64K_LARGE_ROM, 0x2000, 0x30000)]
    [InlineData(SaveType.FLASH_512K_ATMEL_RTC, 0x10000, 0x30000)]
    [InlineData(SaveType.FLASH_512K_ATMEL, 0x10000, 0x30000)]
    [InlineData(SaveType.FLASH_512K_SST_RTC, 0x10000, 0x30000)]
    [InlineData(SaveType.FLASH_512K_SST, 0x10000, 0x30000)]
    [InlineData(SaveType.FLASH_512K_PANASONIC_RTC, 0x10000, 0x30000)]
    [InlineData(SaveType.FLASH_512K_PANASONIC, 0x10000, 0x30000)]
    [InlineData(SaveType.FLASH_1M_MACRONIX_RTC, 0x20000, 0x40400)]
    [InlineData(SaveType.FLASH_1M_MACRONIX, 0x20000, 0x40400)]
    [InlineData(SaveType.FLASH_1M_SANYO_RTC, 0x20000, 0x40400)]
    [InlineData(SaveType.FLASH_1M_SANYO, 0x20000, 0x40400)]
    [InlineData(SaveType.SRAM_256K, 0x8000, 0x30000)]
    [InlineData(SaveType.NONE, 0x0, 0x30000)]
    public void SaveSizesMatchAgbFirm(SaveType type, int saveBytes, int sdSaveSize)
    {
        Assert.Equal(saveBytes, type.SaveBytes());
        Assert.Equal(sdSaveSize, type.SdSaveSize());
    }

    [Theory]
    [InlineData("BTSE", "EEPROM_V120", 0x400, SaveType.EEPROM_8K)]
    [InlineData("BTSE", "EEPROM_V124", 0x400, SaveType.EEPROM_64K)]
    [InlineData("BTSE", "SRAM_V113", 0x400, SaveType.SRAM_256K)]
    [InlineData("BTSE", "SRAM_F_V100", 0x400, SaveType.SRAM_256K)]
    [InlineData("BTSE", "FLASH_V126", 0x400, SaveType.FLASH_512K_PANASONIC)]
    [InlineData("BTSE", "FLASH512_V131", 0x400, SaveType.FLASH_512K_PANASONIC)]
    [InlineData("BTSE", "FLASH1M_V103", 0x400, SaveType.FLASH_1M_MACRONIX)]
    [InlineData("BTSE", "FLASH1M_V103 SIIRTC_V", 0x400, SaveType.FLASH_1M_MACRONIX_RTC)]
    [InlineData("BTSE", "FLASH512_V131 SIIRTC_V", 0x400, SaveType.FLASH_512K_PANASONIC_RTC)]
    [InlineData("BTSE", "", 0x400, SaveType.NONE)]
    [InlineData("BTSE", "EEPROM_V12 SRAM_F_V102", 0x400, SaveType.SRAM_256K)]
    [InlineData("BTSE", "EEPROM_V124", 0x1000010, SaveType.EEPROM_64K_LARGE_ROM)]
    [InlineData("1BCE", "FLASH1M_V103", 0x400, SaveType.EEPROM_64K)]
    public void DetectsSaveType(string gameCode, string signatures, int size, SaveType expected) =>
        Assert.Equal(expected, SaveTypes.Detect(MakeRom(gameCode, signatures, size)));

    [Theory]
    [InlineData("bright", "default", "b7837b930506474a180b68885d458948c4491a9d1b4f3814ad96ea2ac45acb0b")]
    [InlineData("vc", "default", "0d6b5e07de9eae548cd012d40848183c8d56a18f79f796ec61f6b5e2ceb89fd8")]
    [InlineData("corrected", "default", "d0046618205df26f5fb7119a5c972e95056d7ec350b14bbfc0ea2a43f5e810c4")]
    [InlineData("corrected", "off", "974066641b6861cd3eec148d3f3b007c9f3c6dd93791122b5a47c18c57bba680")]
    public void BuildsCodeFooter(string screen, string ghosting, string sha256) =>
        Assert.Equal(sha256, Sha256(GbaFooter.BuildCode(FooterRom(), SaveType.FLASH_1M_MACRONIX_RTC,
            screen, ghosting)));

    [Fact]
    public void BuildsCxi()
    {
        var romFs = new byte[0x400];
        "IVFC"u8.CopyTo(romFs);
        BinaryPrimitives.WriteUInt32LittleEndian(romFs.AsSpan(8), 0x1C0);
        var template = new GbaTemplate([.. Enumerable.Range(0, 0x800).Select(i => (byte)i)], romFs,
            Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("LOGO", 16))));
        var romPath = Path.Combine(_folder, "Test Game (USA).gba");
        File.WriteAllBytes(romPath, FooterRom());
        var cxi = GbaForwarderBuilder.BuildCxi(romPath, new GbaOptions("auto", "corrected", "default"),
            [.. "CBMD"u8, .. Enumerable.Range(0, 60).Select(i => (byte)i)],
            [.. "SMDH"u8, .. Enumerable.Range(0, 100).Select(i => (byte)i)], template, 0xFF401, "BTSE",
            new Progress<string>());
        Assert.Equal("61f0a605f5081e0e8f981d425387abfb1080116d757e65dad1aa02d3a6bf751e", Sha256(cxi));
    }

    // Header with a valid checksum, then each space-separated signature 0x20 bytes apart from 0x200.
    private static byte[] MakeRom(string gameCode, string signatures, int size)
    {
        var rom = new byte[size];
        "TESTGAME"u8.CopyTo(rom.AsSpan(0xA0));
        Encoding.ASCII.GetBytes(gameCode).CopyTo(rom, 0xAC);
        "01"u8.CopyTo(rom.AsSpan(0xB0));
        rom[0xB2] = 0x96;
        rom[0xBD] = (byte)(-rom.AsSpan(0xA0, 0x1D).ToArray().Sum(value => value) - 0x19);
        var at = 0x200;
        foreach (var signature in signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            Encoding.ASCII.GetBytes(signature).CopyTo(rom, at);
            at += 0x20;
        }
        return rom;
    }

    // Odd size so .code pads the ROM to 16 bytes.
    private static byte[] FooterRom() => [.. MakeRom("BTSE", "FLASH1M_V103 SIIRTC_V", 0x400), 1, 2, 3];

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
