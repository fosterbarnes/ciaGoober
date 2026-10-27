using System.Buffers.Binary;
using System.Text;
using CiaGoober.Core;

namespace CiaGoober.Tests;

// Safety contract: a CIA that is not ours is never overwritten, and a failed replace keeps the
// previous file.
public sealed class CiaPublishTests : IDisposable
{
    private const int _ID = 0xFF4CC;
    private const string _SD_PATH = "sd:/roms/nds/Game.nds";
    private readonly string _folder = Directory.CreateTempSubdirectory("ciaGooberTests").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    [Theory]
    [InlineData(0xFF4CD, _SD_PATH)]
    [InlineData(_ID, "sd:/roms/nds/Other.nds")]
    public void RefusesUnrelatedCia(int existingId, string existingSdPath)
    {
        var output = WriteCia("out.cia", existingId, existingSdPath);
        var before = File.ReadAllBytes(output);
        var error = Assert.Throws<IOException>(() =>
            CiaPublisher.Publish(WriteCia("built.cia", _ID, _SD_PATH), output, _ID, _SD_PATH));
        Assert.Contains("Refusing to overwrite unrelated CIA", error.Message);
        Assert.Equal(before, File.ReadAllBytes(output));
    }

    [Fact]
    public void RefusesFileThatIsNotACia()
    {
        var output = Path.Combine(_folder, "out.cia");
        File.WriteAllText(output, "not a cia");
        Assert.Throws<IOException>(() =>
            CiaPublisher.Publish(WriteCia("built.cia", _ID, null), output, _ID, null));
        Assert.Equal("not a cia", File.ReadAllText(output));
    }

    [Fact]
    public void ReplacesOwnCia()
    {
        var output = WriteCia("out.cia", _ID, _SD_PATH);
        var built = WriteCia("built.cia", _ID, _SD_PATH, 1);
        CiaPublisher.Publish(built, output, _ID, _SD_PATH);
        Assert.Equal(File.ReadAllBytes(built), File.ReadAllBytes(output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void FailedReplaceKeepsPreviousCia()
    {
        var output = WriteCia("out.cia", _ID, _SD_PATH);
        var before = File.ReadAllBytes(output);
        var built = WriteCia("built.cia", _ID, _SD_PATH, 1);
        // An open handle without delete sharing makes the rename over the output fail.
        using (new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<UnauthorizedAccessException>(() =>
                CiaPublisher.Publish(built, output, _ID, _SD_PATH));
        Assert.Equal(before, File.ReadAllBytes(output));
        AssertNoTemporaryFiles();
    }

    private void AssertNoTemporaryFiles() => Assert.Equal(["built.cia", "out.cia"],
        Directory.GetFiles(_folder).Select(Path.GetFileName).Order());

    // CiaReaderTests layout (header, then a TMD with a 0x140-byte signature) plus the SD path as
    // content and a filler byte so builds differ.
    private string WriteCia(string name, int uniqueId, string? sdPath, byte filler = 0)
    {
        const int tmd = 0x40, signature = 0x140;
        var content = Encoding.UTF8.GetBytes(sdPath ?? "");
        var cia = new byte[tmd + signature + 0x54 + content.Length + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(cia, 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(cia.AsSpan(16), signature + 0x54);
        BinaryPrimitives.WriteUInt32BigEndian(cia.AsSpan(tmd), 0x10004);
        byte[] titleId = [0, 4, 0, 0, (byte)(uniqueId >> 16), (byte)(uniqueId >> 8), (byte)uniqueId,
            0];
        titleId.CopyTo(cia, tmd + signature + 0x4C);
        content.CopyTo(cia, tmd + signature + 0x54);
        cia[^1] = filler;
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, cia);
        return path;
    }
}
