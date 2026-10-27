using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CiaGoober.ViewModels;

// The SD card in use, shared by the Settings receivers row and DS SD path matching. UI thread
// only; drive reads run on the thread pool, and a newer refresh replaces an older one's result.
public partial class SdCardViewModel : ObservableObject
{
    private int _refreshes;

    // Drive picked with Choose Drive, checked with the removable drives until the app closes.
    public string? ChosenRoot { get; set; }

    // Null = no drive found.
    [ObservableProperty] private SdCardInfo? _info;

    // DS ROMs on Info's drive; null without a readable card.
    [ObservableProperty] private SdRomIndex? _index;

    // Why Info or Index is missing although a drive may be there.
    [ObservableProperty] private string? _error;

    // The chosen drive first, then the first removable drive with TWiLight Menu++, then one that
    // could not be read, then any. Never throws.
    public async Task RefreshAsync()
    {
        var refresh = ++_refreshes;
        var chosen = ChosenRoot;
        (SdCardInfo? Info, SdRomIndex? Index, string? Error) result;
        try
        {
            result = await Task.Run(() => Read(chosen));
        }
        catch (Exception error)
        {
            result = (null, null, $"Could not read the SD cards: {error.Message}");
        }
        if (refresh != _refreshes) return;
        Info = result.Info;
        Index = result.Index;
        Error = result.Error;
    }

    private static (SdCardInfo?, SdRomIndex?, string?) Read(string? chosen)
    {
        var cards = SdCard.FindCards(AppPaths.SdFiles)
            .Where(card => !string.Equals(card.Root, chosen, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (chosen is not null) cards.Insert(0, SdCard.Inspect(chosen, AppPaths.SdFiles));
        var info = cards.FirstOrDefault(card => card.HasTwilight)
            ?? cards.FirstOrDefault(card => card.Error is not null) ?? cards.FirstOrDefault();
        if (info is null) return (null, null, null);
        if (info.Error is not null) return (info, null, $"Could not read {info.Root}: {info.Error}");
        try
        {
            return (info, SdRomIndex.Build(info.Root), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (info, null, $"Could not read the DS ROMs on {info.Root}: {error.Message}");
        }
    }
}
