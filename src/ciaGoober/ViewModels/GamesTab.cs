using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CiaGoober.ViewModels;

// One DS, GBA or GB/GBC tab: its selection, name filter and action bar over MainViewModel.Games.
// UI thread only.
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

    private string? RomFolder
    {
        get => System switch
        {
            GameSystem.Nds => Main.Settings.PcDsRomFolder,
            GameSystem.Gba => Main.Settings.PcGbaRomFolder,
            _ => Main.Settings.PcGbRomFolder,
        };
        set
        {
            switch (System)
            {
                case GameSystem.Nds:
                    Main.Settings.PcDsRomFolder = value;
                    break;
                case GameSystem.Gba:
                    Main.Settings.PcGbaRomFolder = value;
                    break;
                default:
                    Main.Settings.PcGbRomFolder = value;
                    break;
            }
        }
    }

    [RelayCommand]
    private async Task AddRomsAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Add {SystemLabel} ROMs",
            Multiselect = true,
            Filter = System switch
            {
                GameSystem.Nds => "DS ROMs (*.nds)|*.nds",
                GameSystem.Gba => "GBA ROMs (*.gba)|*.gba",
                _ => "GB/GBC ROMs (*.gb;*.gbc)|*.gb;*.gbc",
            },
        };
        if (RomFolder is { } folder && Directory.Exists(folder)) dialog.InitialDirectory = folder;
        if (dialog.ShowDialog() == true) await Main.AddPathsAsync(dialog.FileNames);
    }

    // Scans this tab's PC ROM folder, asking for it when unset.
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (RomFolder is null)
        {
            if (SettingsViewModel.PickFolder($"PC {SystemLabel} ROM Folder", null) is not { } picked)
                return;
            RomFolder = picked;
        }
        var folder = RomFolder!;
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
    private void BuildAll()
    {
        foreach (var game in Games()) Main.BuildQueue.Enqueue(game);
    }

    public List<GameViewModel> Games() => Main.Games.Where(game => game.System == System).ToList();

    private bool HasGames() => Main.Games.Any(game => game.System == System);
}
