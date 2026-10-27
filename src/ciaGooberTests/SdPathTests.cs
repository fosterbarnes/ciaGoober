using CiaGoober.Core;

namespace CiaGoober.Tests;

public class SdPathTests
{
    [Theory]
    [InlineData("/roms/x.nds", "sd:/roms/x.nds")]
    [InlineData("SD:/Roms/Game.NDS", "sd:/Roms/Game.NDS")]
    [InlineData("sd:/Pok\u0065\u0301mon.nds", "sd:/Pok\u00e9mon.nds")]
    public void CanonicalAcceptsCardPaths(string value, string expected) =>
        Assert.Equal(expected, SdPath.Canonical(value));

    [Theory]
    [InlineData("roms/x.nds")]
    [InlineData("J:/roms/x.nds")]
    [InlineData("sd:/roms//x.nds")]
    [InlineData("sd:/../x.nds")]
    [InlineData("sd:/./x.nds")]
    [InlineData("sd:/roms./x.nds")]
    [InlineData("sd:/roms /x.nds")]
    [InlineData("sd:/ro:ms/x.nds")]
    [InlineData("sd:/roms\\x.nds")]
    [InlineData("sd:/roms/x?.nds")]
    [InlineData("sd:/roms/x\u0085.nds")]
    [InlineData("sd:/roms/.nds")]
    [InlineData("sd:/roms/x.gba")]
    public void CanonicalRejectsInvalidPaths(string value) =>
        Assert.Throws<ArgumentException>(() => SdPath.Canonical(value));

    [Fact]
    public void CanonicalLimitsUtf8Bytes()
    {
        // "sd:/a" + 123 two-byte chars + ".nds" = 255 bytes; "sd:/ab..." is 256.
        var name = new string('\u00e9', 123);
        Assert.Equal(255, System.Text.Encoding.UTF8.GetByteCount(SdPath.Canonical($"sd:/a{name}.nds")));
        Assert.Throws<ArgumentException>(() => SdPath.Canonical($"sd:/ab{name}.nds"));
    }

    [Fact]
    public void ResolvePrefersOverrideThenFolderThenDrive()
    {
        Assert.Equal("sd:/games/y.nds", SdPath.Resolve(@"J:\roms\x.nds", @" \games\y.nds ", "sd:/roms"));
        Assert.Equal("sd:/nds/x.nds", SdPath.Resolve(@"J:\roms\x.nds", "  ", @"/nds\\"));
        Assert.Equal("sd:/roms/x.nds", SdPath.Resolve(@"J:\roms\x.nds", null, ""));
    }

    [Theory]
    [InlineData(@"C:\roms\x.nds")]
    [InlineData(@"c:\roms\x.nds")]
    [InlineData(@"roms\x.nds")]
    [InlineData(@"\\server\share\x.nds")]
    public void ResolveNeverUsesThePcDrive(string romPath)
    {
        var error = Assert.Throws<ArgumentException>(() => SdPath.Resolve(romPath, null, null));
        Assert.Contains("Set the DS ROM Folder", error.Message);
    }

    [Fact]
    public void FromCardFolderMapsDrivePaths()
    {
        Assert.Equal("sd:/roms/nds", SdPath.FromCardFolder(@"J:\roms\nds\"));
        Assert.Equal("sd:/", SdPath.FromCardFolder(@"J:\"));
        Assert.Throws<ArgumentException>(() => SdPath.FromCardFolder(@"C:\roms"));
        Assert.Throws<ArgumentException>(() => SdPath.FromCardFolder("roms"));
    }
}
