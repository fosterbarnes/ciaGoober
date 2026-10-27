using System.Buffers.Binary;
using System.Text;
using CiaGoober.Core;

namespace CiaGoober.Tests;

// A wrong match makes a forwarder launch another game and keys the registry by the wrong path;
// a copy that replaces a card file destroys the user's ROM or save pairing.
public sealed class SdRomTests : IDisposable
{
    private readonly string _card = Directory.CreateTempSubdirectory("ciaGooberCard").FullName;

    public void Dispose() => Directory.Delete(_card, true);

    [Fact]
    public void MatchNeverMapsASameNamedFileWithAnotherHeader()
    {
        WriteRom(Path.Combine(_card, "roms", "Game.nds"), "AAAE", 0x1111);
        WriteRom(Path.Combine(_card, "other", "Renamed.nds"), "BBBE", 0x2222);
        WriteRom(Path.Combine(_card, "_nds", "Game.nds"), "BBBE", 0x3333);
        var index = SdRomIndex.Build(_card);

        Assert.Equal(SdMatchKind.Missing,
            index.Match(@"F:\pc\Game.nds", new NdsIdentity("BBBE", 0x3333)).Kind);
        Assert.Equal("sd:/other/Renamed.nds",
            index.Match(@"F:\pc\Game.nds", new NdsIdentity("BBBE", 0x2222)).SdPath);
        Assert.Equal("sd:/roms/Game.nds",
            index.Match(@"F:\pc\game.NDS", new NdsIdentity("AAAE", 0x1111)).SdPath);
    }

    [Fact]
    public void MatchIsAmbiguousForTwoFilesWithTheSameHeaderAndOtherNames()
    {
        WriteRom(Path.Combine(_card, "a.nds"), "AAAE", 0x1111);
        WriteRom(Path.Combine(_card, "b.nds"), "AAAE", 0x1111);

        var match = SdRomIndex.Build(_card).Match(@"F:\pc\c.nds", new NdsIdentity("AAAE", 0x1111));

        Assert.Equal(SdMatchKind.Ambiguous, match.Kind);
        Assert.Null(match.SdPath);
    }

    [Fact]
    public async Task CopyNeverReplacesAFileAndLeavesNoTempFile()
    {
        var source = Path.Combine(_card, "source.nds");
        var target = Path.Combine(_card, "roms", "x.nds");
        WriteRom(source, "AAAE", 0x1111);
        WriteRom(target, "BBBE", 0x2222);
        var original = File.ReadAllBytes(target);

        await Assert.ThrowsAsync<IOException>(() =>
            SdRomCopy.CopyAsync(source, target, new Progress<long>(), CancellationToken.None));

        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
    }

    [Fact]
    public async Task CancelledCopyLeavesNoFile()
    {
        var source = Path.Combine(_card, "source.nds");
        var target = Path.Combine(_card, "roms", "x.nds");
        WriteRom(source, "AAAE", 0x1111);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SdRomCopy.CopyAsync(source, target, new Progress<long>(), new CancellationToken(true)));

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!));
    }

    private static void WriteRom(string path, string gameCode, ushort headerCrc)
    {
        var header = new byte[0x200];
        Encoding.ASCII.GetBytes(gameCode).CopyTo(header, 0x0C);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x15E), headerCrc);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, header);
    }
}
