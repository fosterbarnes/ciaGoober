using CiaGoober.Core;

namespace CiaGoober.Tests;

// Safety contract: receiver installs back up what they replace, verify the copies, never touch a
// card without TWiLight Menu++, and leave the card as it was when a copy fails.
public sealed class SdCardTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ciaGooberTests").FullName;

    public SdCardTests()
    {
        Directory.CreateDirectory(Bundled);
        foreach (var name in SdCard.RECEIVERS) File.WriteAllText(Path.Combine(Bundled, name), $"new {name}");
    }

    public void Dispose() => Directory.Delete(_folder, true);

    private string Bundled => Path.Combine(_folder, "sd");
    private string Card => Path.Combine(_folder, "card");
    private string Backups => Path.Combine(_folder, "backup");
    private string Twilight => Path.Combine(Card, SdCard.TWILIGHT_FOLDER);

    [Fact]
    public void InstallBacksUpReplacedFilesAndVerifiesCopies()
    {
        Directory.CreateDirectory(Twilight);
        File.WriteAllText(Path.Combine(Twilight, "main.srldr"), "old main");

        var backup = SdCard.Install(Card, Bundled, Backups);

        Assert.NotNull(backup);
        var saved = Path.Combine(backup, SdCard.TWILIGHT_FOLDER);
        Assert.Equal("old main", File.ReadAllText(Path.Combine(saved, "main.srldr")));
        Assert.False(File.Exists(Path.Combine(saved, "dsimenu.srldr")));
        foreach (var name in SdCard.RECEIVERS)
            Assert.Equal(SdCard.Hash(Path.Combine(Bundled, name)), SdCard.Hash(Path.Combine(Twilight, name)));
        Assert.Equal(SdCard.RECEIVERS.Order(), Directory.GetFiles(Twilight).Select(Path.GetFileName).Order());
        Assert.All(SdCard.Inspect(Card, Bundled).Receivers, r => Assert.Equal(ReceiverState.Current, r.State));
        Assert.Null(SdCard.Install(Card, Bundled, Backups));
    }

    [Fact]
    public void FailedCopyLeavesOriginalFiles()
    {
        Directory.CreateDirectory(Twilight);
        foreach (var name in SdCard.RECEIVERS) File.WriteAllText(Path.Combine(Twilight, name), $"old {name}");
        // main.srldr is replaced first; the open handle makes replacing dsimenu.srldr fail.
        using (new FileStream(Path.Combine(Twilight, "dsimenu.srldr"), FileMode.Open, FileAccess.Read,
            FileShare.Read))
        {
            Assert.Throws<IOException>(() => SdCard.Install(Card, Bundled, Backups));
        }
        foreach (var name in SdCard.RECEIVERS)
            Assert.Equal($"old {name}", File.ReadAllText(Path.Combine(Twilight, name)));
        Assert.Equal(SdCard.RECEIVERS.Order(), Directory.GetFiles(Twilight).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void RefusesCardWithoutTwilight()
    {
        Directory.CreateDirectory(Card);
        Assert.False(SdCard.Inspect(Card, Bundled).HasTwilight);
        Assert.Throws<InvalidOperationException>(() => SdCard.Install(Card, Bundled, Backups));
        Assert.Empty(Directory.GetFileSystemEntries(Card));
        Assert.False(Directory.Exists(Backups));
    }
}
