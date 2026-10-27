using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CiaGoober.ViewModels;

// One DS, GBA, GB/GBC, NES or SNES tab: its selection, name filter and action bar over
// MainViewModel.Games. UI thread only.
public partial class GamesTabViewModel : ObservableObject
{
    public GamesTabViewModel(MainViewModel main, GameSystem system)
    {
        Main = main;
        System = system;
        main.Games.CollectionChanged += (_, _) =>
        {
            AutoFillAllCommand.NotifyCanExecuteChanged();
            BuildAllCommand.NotifyCanExecuteChanged();
        };
    }

    public MainViewModel Main { get; }
    public GameSystem System { get; }
    public string SystemLabel => GameViewModel.Label(System);

    [ObservableProperty] private GameViewModel? _selectedGame;
    [ObservableProperty] private string _filter = "";

    // Opens at the deepest folder holding this system's ROMs inside the PC ROM Folder
    // (F:\ROMS\Game Boy Advance for GBA), else at the PC ROM Folder itself.
    [RelayCommand]
    private async Task AddRomsAsync()
    {
        var patterns = string.Join(';', RomScanner.Extensions(System).Select(ext => "*" + ext));
        var dialog = new OpenFileDialog
        {
            Title = $"Add {SystemLabel} ROMs",
            Multiselect = true,
            Filter = $"{SystemLabel} ROMs ({patterns})|{patterns}",
        };
        if (Main.Settings.PcRomFolder is { } root && Directory.Exists(root))
        {
            var system = System;
            try
            {
                dialog.InitialDirectory = await Task.Run(() =>
                    RomScanner.CommonFolder(RomScanner.Scan(root, system)) ?? root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Only the starting folder depends on the scan; Scan reports the problem.
                dialog.InitialDirectory = root;
            }
        }
        if (dialog.ShowDialog() == true) await Main.AddPathsAsync(dialog.FileNames);
    }

    // Scans the PC ROM Folder for this tab's system, asking for the folder when unset.
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (Main.Settings.PcRomFolder is null)
        {
            if (SettingsViewModel.PickFolder("PC ROM Folder", null) is not { } picked) return;
            Main.Settings.PcRomFolder = picked;
        }
        var folder = Main.Settings.PcRomFolder!;
        var system = System;
        List<string> found;
        try
        {
            found = await Task.Run(() => RomScanner.Scan(folder, system).ToList());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Main.ShowStatus($"Could not scan the ROM folder: {error.Message}");
            return;
        }
        var (message, _) = await Main.AddFilesAsync(found, "Scan Auto-Fill finished");
        Main.ShowStatus("Scan finished: " + message);
    }

    // Fills only missing banners and icons, like a scan.
    [RelayCommand(CanExecute = nameof(HasGames))]
    private void AutoFillAll() => Main.QueueAutoFill(Games(), "Auto-Fill finished");

    [RelayCommand(CanExecute = nameof(HasGames))]
    private Task BuildAllAsync() => Main.BuildGamesAsync(Games());

    public List<GameViewModel> Games() => Main.Games.Where(game => game.System == System).ToList();

    private bool HasGames() => Main.Games.Any(game => game.System == System);
}
