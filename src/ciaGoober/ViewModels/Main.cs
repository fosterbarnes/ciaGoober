using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CiaGoober.ViewModels;

// State shared by every tab: the game list, builds, Auto-Fill runs and the status line. Each game
// tab's own state is a GamesTabViewModel. Every member runs on the UI thread; scans and ROM reads
// run on the thread pool.
public partial class MainViewModel : ObservableObject
{
    private const int _AUTO_FILL_AT_ONCE = 3;
    private const int _COPY_NAMES_SHOWN = 10;
    private const double _GIGABYTE = 1 << 30;
    private static readonly TimeSpan _STATUS_LIFETIME = TimeSpan.FromSeconds(15);

    private readonly Queue<(GameViewModel Game, FillReport Report)> _fillWaiting = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _statusTimer;
    private int _fillRunning;

    public MainViewModel(SettingsViewModel settings, ForwarderRegistry registry)
    {
        Settings = settings;
        Registry = registry;
        BuildQueue = new BuildQueue(settings, registry);
        BuildQueue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(BuildQueue.Pending)) return;
            ResetAllCommand.NotifyCanExecuteChanged();
            ResetTitleIdsCommand.NotifyCanExecuteChanged();
            ResetLaunchBoxDataCommand.NotifyCanExecuteChanged();
        };
        Games.CollectionChanged += Games_CollectionChanged;
        _statusTimer = new DispatcherTimer { Interval = _STATUS_LIFETIME };
        _statusTimer.Tick += (_, _) => DismissStatus();
        Ds = new GamesTabViewModel(this, GameSystem.Nds);
        Gba = new GamesTabViewModel(this, GameSystem.Gba);
        Gb = new GamesTabViewModel(this, GameSystem.Gb);
        Nes = new GamesTabViewModel(this, GameSystem.Nes);
        Snes = new GamesTabViewModel(this, GameSystem.Snes);
    }

    public SettingsViewModel Settings { get; }
    public ForwarderRegistry Registry { get; }
    public BuildQueue BuildQueue { get; }
    public ObservableCollection<GameViewModel> Games { get; } = [];
    public GamesTabViewModel Ds { get; }
    public GamesTabViewModel Gba { get; }
    public GamesTabViewModel Gb { get; }
    public GamesTabViewModel Nes { get; }
    public GamesTabViewModel Snes { get; }

    public GamesTabViewModel Tab(GameSystem system) => system switch
    {
        GameSystem.Nds => Ds,
        GameSystem.Gba => Gba,
        GameSystem.Gb => Gb,
        GameSystem.Nes => Nes,
        GameSystem.Snes => Snes,
        _ => throw new ArgumentOutOfRangeException(nameof(system), system, null),
    };

    // Latest run summary or error; clears itself after a while.
    [ObservableProperty] private string? _statusMessage;

    // Adds dropped files and folders (folders are scanned for every system); returns the system
    // of the first added game, or null when none was added.
    public async Task<GameSystem?> AddPathsAsync(IReadOnlyList<string> paths)
    {
        var (files, problems, skipped) = await Task.Run(() => ExpandPaths(paths));
        var (message, first) = await AddFilesAsync(files, "Auto-Fill finished");
        if (skipped > 0)
            message += $". Skipped {skipped} items that are not "
                + $"{string.Join(", ", RomScanner.Extensions(null))} or a folder";
        if (problems.Count > 0) message += ". Could not scan: " + string.Join("; ", problems);
        ShowStatus(message);
        return first;
    }

    // Stops builds and LaunchBox lookups; the window calls it when closing. The timer stops so a
    // closing message stays up while running builds wind down.
    public void Shutdown()
    {
        _fillWaiting.Clear();
        _closing.Cancel();
        BuildQueue.CancelAll();
        _statusTimer.Stop();
    }

    [RelayCommand]
    private void DismissStatus()
    {
        _statusTimer.Stop();
        StatusMessage = null;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task BuildAsync(GameViewModel? game)
    {
        if (game is not null) await BuildGamesAsync([game]);
    }

    // Queues the games. DS games first get a fresh SD card check: without a 3DS SD Card ROM
    // Folder the user picks one on the card, and ROMs missing from the card are copied there when
    // the user agrees. Declining or a failed copy still builds, against the expected path.
    internal async Task BuildGamesAsync(IReadOnlyList<GameViewModel> games)
    {
        if (games.Any(game => game.IsDs))
        {
            await Settings.Card.RefreshAsync();
            if (Settings.Card.Index is { } index)
            {
                if (Settings.SdRomFolder is null
                    && games.Any(game => game.IsDs && game.ResolvedSdPath is null))
                    PickCardRomFolder(index.CardRoot);
                var copies = games.Where(game => game.SdCopyTarget is not null && IsIdle(game))
                    .ToList();
                if (copies.Count > 0 && !await CopyToCardAsync(copies, index.CardRoot)) return;
            }
        }
        foreach (var game in games) BuildQueue.Enqueue(game);
    }

    private void PickCardRomFolder(string cardRoot)
    {
        if (SettingsViewModel.PickFolder($"3DS SD Card ROM Folder on {cardRoot}", cardRoot)
            is not { } folder) return;
        try
        {
            Settings.SdRomFolder = SdPath.FromCardFolder(folder);
        }
        catch (ArgumentException error)
        {
            ShowStatus(error.Message);
        }
    }

    // Copies one game at a time; false when closing the app cancelled the copy.
    private async Task<bool> CopyToCardAsync(IReadOnlyList<GameViewModel> games, string root)
    {
        List<(GameViewModel Game, string Target, long Size)> items;
        long free;
        try
        {
            items = games.Select(game => (game, SdPath.ToCardFile(root, game.SdCopyTarget!),
                new FileInfo(game.RomPath).Length)).ToList();
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"DS ROMs not copied: {error.Message}");
            return true;
        }
        var total = items.Sum(item => item.Size);
        if (total > free)
        {
            ShowStatus($"DS ROMs not copied: they need {Size(total)} and {root} has {Size(free)} "
                + "free");
            return true;
        }
        var names = string.Join("\n", items.Take(_COPY_NAMES_SHOWN)
            .Select(item => Path.GetFileName(item.Game.RomPath)));
        if (items.Count > _COPY_NAMES_SHOWN) names += $"\nand {items.Count - _COPY_NAMES_SHOWN} more";
        if (MessageBox.Show(Application.Current.MainWindow,
            $"Copy {items.Count} DS ROMs ({Size(total)}) to the SD card in {root}? "
            + $"{Size(free)} free.\n\n{names}", "ciaGoober: SD card", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return true;

        IsCopying = true;
        var failed = new List<string>();
        long done = 0;
        try
        {
            foreach (var (item, number) in items.Select((item, n) => (item, n + 1)))
            {
                var name = Path.GetFileName(item.Game.RomPath);
                var before = done;
                var progress = new Progress<long>(copied =>
                {
                    // Reports are posted, so one may land after the copy finished.
                    if (IsCopying)
                        ShowStatus($"Copying {name} to {root} ({number} of {items.Count}, "
                            + $"{(before + copied) * 100 / Math.Max(1, total)}%)");
                });
                try
                {
                    await SdRomCopy.CopyAsync(item.Game.RomPath, item.Target, progress,
                        _closing.Token);
                }
                catch (OperationCanceledException) when (_closing.IsCancellationRequested)
                {
                    return false;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    failed.Add($"{name} ({error.Message})");
                }
                done += item.Size;
            }
        }
        finally
        {
            IsCopying = false;
        }
        await Settings.Card.RefreshAsync();
        ShowStatus(failed.Count == 0 ? $"Copied {items.Count} DS ROMs to {root}"
            : $"Copied {items.Count - failed.Count} of {items.Count} DS ROMs to {root}. "
                + "Could not copy: " + string.Join("; ", failed));
        return true;
    }

    private static string Size(long bytes) => $"{bytes / _GIGABYTE:0.0} GB";

    // True while DS ROMs are copied to the SD card; the window waits for it when closing.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetAllCommand), nameof(ResetTitleIdsCommand),
        nameof(ResetLaunchBoxDataCommand))]
    private bool _isCopying;

    // Every setting back to its default and the imported GBA, GB/GBC, NES and SNES donor
    // templates removed. The title ID registry stays, so installed forwarders keep their IDs, and so does
    // the LaunchBox data. Not while builds or SD copies run, since they read both.
    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAllAsync()
    {
        if (MessageBox.Show(Application.Current.MainWindow,
            "Reset ciaGoober to defaults?\n\nEvery setting goes back to its default and the "
            + "imported GBA, GB/GBC, NES and SNES donor templates are deleted; import them again "
            + "to build those systems. The title ID registry and LaunchBox data are kept.",
            "ciaGoober: Reset to Defaults", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Settings.ResetToDefaults();
        try
        {
            await Task.Run(() =>
            {
                GbaTemplate.Remove(AppPaths.GbaTemplate);
                VcTemplate.Remove(AppPaths.GbTemplate);
                VcTemplate.Remove(AppPaths.NesTemplate);
                VcTemplate.Remove(AppPaths.SnesTemplate);
            });
            Settings.ResetMessage = "Settings reset and donor templates deleted";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Settings.ResetMessage = $"Settings reset; donor templates not deleted: {error.Message}";
        }
        await Settings.RefreshPageStateAsync();
    }

    // Overrides the never-reset rule on user request: the next builds reuse IDs from 0xFF400, so
    // installing them replaces any forwarder already on the console with the same ID. The old
    // file is kept in AppPaths.RegistryBackup.
    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetTitleIdsAsync()
    {
        if (MessageBox.Show(Application.Current.MainWindow,
            $"Reset the title ID registry?\n\nThe {Registry.Entries().Count} recorded forwarders "
            + "are forgotten and new builds start again at title ID 0xFF400. Installing a new CIA "
            + "on a console that still has an older forwarder with the same ID replaces that "
            + "forwarder with a different game. Uninstall the old forwarders first, or keep the "
            + "registry.\n\nThe current registry is moved to " + AppPaths.RegistryBackup + ".",
            "ciaGoober: Reset Title IDs", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            var backup = await Registry.ResetAsync(AppPaths.RegistryBackup);
            Settings.ResetMessage = backup is null ? "Title IDs reset; there was no registry yet"
                : $"Title IDs reset; old registry saved to {backup}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Settings.ResetMessage = $"Title IDs not reset: {error.Message}";
        }
    }

    // Deletes the refreshed LaunchBox index and downloaded art. Listed games drop art chosen
    // from that cache, since the files are gone. Not while Auto-Fill or a refresh writes there.
    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetLaunchBoxDataAsync()
    {
        if (_fillRunning > 0 || _fillWaiting.Count > 0
            || Settings.RefreshLaunchBoxCommand.IsRunning)
        {
            Settings.ResetMessage = "LaunchBox data not reset: wait for Auto-Fill and the "
                + "LaunchBox refresh to finish";
            return;
        }
        if (MessageBox.Show(Application.Current.MainWindow,
            "Reset the LaunchBox data?\n\nThe refreshed LaunchBox index and all downloaded art are "
            + "deleted. Auto-Fill uses the bundled index and downloads art again. Listed games "
            + "lose banners and icons that came from LaunchBox.",
            "ciaGoober: Reset LaunchBox Data", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(LaunchBoxIndex.DeleteData);
            Settings.ResetMessage = "LaunchBox data reset";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Settings.ResetMessage = $"LaunchBox data not fully reset: {error.Message}";
        }
        var cache = Path.TrimEndingDirectorySeparator(AppPaths.LaunchBox)
            + Path.DirectorySeparatorChar;
        bool Cached(ArtChoice art) => art.Source is { } source
            && Path.GetFullPath(source).StartsWith(cache, StringComparison.OrdinalIgnoreCase);
        foreach (var game in Games)
        {
            if (Cached(game.Banner)) game.Banner = new ArtChoice(null, null);
            if (Cached(game.Icon)) game.Icon = new ArtChoice(null, null);
        }
        await Settings.RefreshPageStateAsync();
    }

    private bool CanReset() => BuildQueue.Pending == 0 && !IsCopying;

    // Selects the next game of the same system in its tab, else the previous one.
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Remove(GameViewModel? game)
    {
        if (game is null || !IsIdle(game)) return;
        var tab = Tab(game.System);
        var siblings = tab.Games();
        var index = siblings.IndexOf(game);
        if (index < 0) return;
        var wasSelected = tab.SelectedGame == game;
        Games.Remove(game);
        siblings.RemoveAt(index);
        if (wasSelected)
            tab.SelectedGame = siblings.Count == 0 ? null : siblings[Math.Min(index, siblings.Count - 1)];
        ShowStatus($"Removed {game.DisplayName}");
    }

    private static bool IsIdle(GameViewModel? game) =>
        game is { Status: not (GameStatus.Queued or GameStatus.Building) };

    internal void ShowStatus(string message)
    {
        StatusMessage = message;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // Lists ROMs not listed yet (same full path, any case) and queues their Auto-Fill; returns
    // the summary and the first added game's system. Rechecks after the reads, since another add
    // may have listed them meanwhile.
    internal async Task<(string Message, GameSystem? First)> AddFilesAsync(
        IReadOnlyList<string> files, string fillTitle)
    {
        var listed = ListedPaths();
        var candidates = files.Where(file => listed.Add(Path.GetFullPath(file))).ToList();
        var reads = await Task.Run(() => candidates.Select(ReadRom).ToList());
        listed = ListedPaths();
        var added = new List<GameViewModel>();
        var errors = new List<string>();
        foreach (var (path, rom, error) in reads)
        {
            if (rom is null)
                errors.Add($"{Path.GetFileName(path)} ({error})");
            else if (listed.Add(Path.GetFullPath(path)))
                added.Add(new GameViewModel(rom, Settings));
        }
        foreach (var game in added) Games.Add(game);
        foreach (var tab in (GamesTabViewModel[])[Ds, Gba, Gb, Nes, Snes])
            tab.SelectedGame ??= added.FirstOrDefault(game => game.System == tab.System);
        QueueAutoFill(added, fillTitle);

        var known = files.Count - added.Count - errors.Count;
        var counts = ((GamesTabViewModel[])[Ds, Gba, Gb, Nes, Snes])
            .Select(tab => (tab.SystemLabel, Count: added.Count(game => game.System == tab.System)))
            .Where(count => count.Count > 0).Select(count => $"{count.Count} {count.SystemLabel}")
            .ToList();
        var message = counts.Count switch
        {
            0 => "Added 0 ROMs",
            1 => $"Added {counts[0]} ROMs",
            _ => $"Added {string.Join(", ", counts.SkipLast(1))} and {counts[^1]} ROMs",
        } + $" ({known} already listed)";
        if (errors.Count > 0)
            message += $". Could not read {errors.Count}: " + string.Join("; ", errors);
        // DS ROMs map to 3DS SD Card ROM Folder + file name and the others register by file
        // name, so equal names in different folders collide.
        var shared = Games
            .GroupBy(game => (game.System, Path.GetFileName(game.RomPath).ToLowerInvariant()))
            .Where(group => group.Count() > 1 && group.Any(added.Contains))
            .Select(group => Path.GetFileName(group.First().RomPath)).ToList();
        if (shared.Count > 0)
            message += ". Same file name in more than one folder: " + string.Join(", ", shared);
        return (message, added.FirstOrDefault()?.System);
    }

    private HashSet<string> ListedPaths() =>
        new(Games.Select(game => Path.GetFullPath(game.RomPath)), StringComparer.OrdinalIgnoreCase);

    private static (string Path, RomInfo? Rom, string? Error) ReadRom(string path)
    {
        try
        {
            var rom = RomScanner.SystemOf(Path.GetExtension(path)) switch
            {
                GameSystem.Gba => GbaRom.Read(path),
                GameSystem.Gb => GbRom.Read(path),
                GameSystem.Nes => NesRom.Read(path),
                GameSystem.Snes => SnesRom.Read(path),
                _ => NdsRom.Read(path),
            };
            return (path, rom, null);
        }
        catch (Exception error)
        {
            return (path, null, error.Message);
        }
    }

    private static (List<string> Files, List<string> Problems, int Skipped) ExpandPaths(
        IReadOnlyList<string> paths)
    {
        var files = new List<string>();
        var problems = new List<string>();
        var skipped = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    files.AddRange(RomScanner.Scan(path, null));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{path} ({error.Message})");
                }
            }
            else if (RomScanner.SystemOf(Path.GetExtension(path)) is not null)
                files.Add(path);
            else
                skipped++;
        }
        return (files, problems, skipped);
    }

    private void Games_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (GameViewModel game in e.OldItems ?? Array.Empty<GameViewModel>())
            game.PropertyChanged -= Game_PropertyChanged;
        foreach (GameViewModel game in e.NewItems ?? Array.Empty<GameViewModel>())
            game.PropertyChanged += Game_PropertyChanged;
    }

    private void Game_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GameViewModel.Status)) return;
        BuildCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    // Scanned and added ROMs get LaunchBox art, three games at a time, one summary per run
    // (game-list.js _runLogoQueue / _reportLogo).
    internal void QueueAutoFill(IReadOnlyList<GameViewModel> games, string title)
    {
        if (games.Count == 0) return;
        var report = new FillReport(title, games.Count);
        foreach (var game in games) _fillWaiting.Enqueue((game, report));
        StartAutoFill();
    }

    private void StartAutoFill()
    {
        while (_fillRunning < _AUTO_FILL_AT_ONCE && _fillWaiting.TryDequeue(out var item))
            _ = AutoFillAsync(item.Game, item.Report);
    }

    // Never faults: every outcome lands in the report.
    private async Task AutoFillAsync(GameViewModel game, FillReport report)
    {
        _fillRunning++;
        var outcome = (Banner: FillOutcome.Kept, Icon: FillOutcome.Kept);
        try
        {
            if (Games.Contains(game)) outcome = await FillMissingArtAsync(game);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            outcome = (FillOutcome.Failed, FillOutcome.Failed);
        }
        finally
        {
            _fillRunning--;
        }
        if (report.Add(outcome.Banner, outcome.Icon)) ShowStatus(report.Summary());
        StartAutoFill();
    }

    private async Task<(FillOutcome Banner, FillOutcome Icon)> FillMissingArtAsync(GameViewModel game)
    {
        var banner = game.Banner.Source is null;
        var icon = game.Icon.Source is null && !game.UseRomIcon;
        if (!banner && !icon) return (FillOutcome.Kept, FillOutcome.Kept);
        var result = await game.AutoFillArtworkAsync(false, _closing.Token);
        return (Outcome(banner, result.BannerPath, result.BannerError),
            Outcome(icon, result.IconPath, result.IconError));
    }

    private static FillOutcome Outcome(bool wanted, string? path, Exception? error) =>
        !wanted ? FillOutcome.Kept
        : error is not null ? FillOutcome.Failed
        : path is not null ? FillOutcome.Set
        : FillOutcome.Unmatched;

    private enum FillOutcome { Set, Unmatched, Kept, Failed }

    private sealed class FillReport(string title, int pending)
    {
        private readonly int[] _banner = new int[4];
        private readonly int[] _icon = new int[4];
        private int _pending = pending;

        // True when this was the last game of the run.
        public bool Add(FillOutcome banner, FillOutcome icon)
        {
            _banner[(int)banner]++;
            _icon[(int)icon]++;
            return --_pending == 0;
        }

        public string Summary()
        {
            string Both(FillOutcome outcome) =>
                $"{_banner[(int)outcome]} banners and {_icon[(int)outcome]} icons";
            bool Any(FillOutcome outcome) => _banner[(int)outcome] + _icon[(int)outcome] > 0;
            var parts = new List<string>
            {
                Both(FillOutcome.Set) + " set from LaunchBox",
                Both(FillOutcome.Unmatched) + " without a match",
            };
            if (Any(FillOutcome.Kept)) parts.Add(Both(FillOutcome.Kept) + " kept");
            if (Any(FillOutcome.Failed)) parts.Add(Both(FillOutcome.Failed) + " failed");
            return $"{title}: {string.Join(", ", parts)}";
        }
    }
}
