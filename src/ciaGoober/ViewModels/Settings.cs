using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CiaGoober.ViewModels;

// App settings, saved to the store file on every change; nothing per game is stored. Also runs
// the Settings page actions (folder pickers, SD receivers, GBA, GB/GBC and NES donor import,
// LaunchBox refresh).
public partial class SettingsViewModel : ObservableObject
{
    public const int MIN_PARALLEL_BUILDS = 1;
    public const int MAX_PARALLEL_BUILDS = 4;
    private const int _DEFAULT_PARALLEL_BUILDS = 3;
    private const string _WHITE = "#FFFFFF";
    private const double _MEGABYTE = 1 << 20;

    private static readonly HashSet<string> _PERSISTED =
    [
        nameof(CiaOutputFolder), nameof(PcDsRomFolder), nameof(PcGbaRomFolder),
        nameof(PcGbRomFolder), nameof(PcNesRomFolder), nameof(SdRomFolder),
        nameof(DefaultIconBackground),
        nameof(ParallelBuilds), nameof(SelectedPage),
    ];

    // Null until Load attaches the file, so applying loaded values does not write it back.
    private string? _storePath;
    private string _ciaOutputFolder = AppPaths.DefaultCiaOutput;
    private string? _pcDsRomFolder;
    private string? _pcGbaRomFolder;
    private string? _pcGbRomFolder;
    private string? _pcNesRomFolder;
    private string? _sdRomFolder;
    private string? _defaultIconBackground;
    private int _parallelBuilds = _DEFAULT_PARALLEL_BUILDS;
    private string? _selectedPage;

    // Drive picked with Choose Drive, checked with the removable drives until the app closes.
    private string? _chosenCardRoot;

    // Defaults that are never saved.
    public SettingsViewModel()
    {
    }

    public static SettingsViewModel Load(string path)
    {
        var (data, problem) = SettingsStore.Load(path);
        var settings = new SettingsViewModel();
        List<string> problems = problem is null ? [] : [problem];
        settings.CiaOutputFolder = data.CiaOutputFolder ?? AppPaths.DefaultCiaOutput;
        settings.PcDsRomFolder = data.PcDsRomFolder;
        settings.PcGbaRomFolder = data.PcGbaRomFolder;
        settings.PcGbRomFolder = data.PcGbRomFolder;
        settings.PcNesRomFolder = data.PcNesRomFolder;
        settings.SdRomFolder = data.SdRomFolder;
        settings.ParallelBuilds = data.ParallelBuilds ?? _DEFAULT_PARALLEL_BUILDS;
        settings.SelectedPage = data.SelectedPage;
        try
        {
            settings.DefaultIconBackground = data.DefaultIconBackground;
        }
        catch (ArgumentException)
        {
            problems.Add($"Default Icon Background in {path} is not #RRGGBB, white is in use.");
        }
        settings._storePath = path;
        settings.StoreError = problems.Count == 0 ? null : string.Join(" ", problems);
        return settings;
    }

    // Empty resets to Documents\ciaGoober\cia.
    public string CiaOutputFolder
    {
        get => _ciaOutputFolder;
        set => SetProperty(ref _ciaOutputFolder, Clean(value) ?? AppPaths.DefaultCiaOutput);
    }

    public string? PcDsRomFolder
    {
        get => _pcDsRomFolder;
        set => SetProperty(ref _pcDsRomFolder, Clean(value));
    }

    public string? PcGbaRomFolder
    {
        get => _pcGbaRomFolder;
        set => SetProperty(ref _pcGbaRomFolder, Clean(value));
    }

    public string? PcGbRomFolder
    {
        get => _pcGbRomFolder;
        set => SetProperty(ref _pcGbRomFolder, Clean(value));
    }

    public string? PcNesRomFolder
    {
        get => _pcNesRomFolder;
        set => SetProperty(ref _pcNesRomFolder, Clean(value));
    }

    // DS ROM folder on the SD card in "sd:/..." form; null = the ROM's own drive.
    public string? SdRomFolder
    {
        get => _sdRomFolder;
        set
        {
            if (SetProperty(ref _sdRomFolder, Clean(value))) SdRomFolderError = null;
        }
    }

    // "#RRGGBB"; null = white. Throws ArgumentException for any other text.
    public string? DefaultIconBackground
    {
        get => _defaultIconBackground;
        set
        {
            var color = Clean(value) is { } text ? IconColor.Normalize(text) : null;
            if (!SetProperty(ref _defaultIconBackground, color)) return;
            OnPropertyChanged(nameof(EffectiveIconBackground));
            OnPropertyChanged(nameof(IconBackgroundText));
        }
    }

    public string EffectiveIconBackground => DefaultIconBackground ?? _WHITE;

    public int ParallelBuilds
    {
        get => _parallelBuilds;
        set => SetProperty(ref _parallelBuilds,
            Math.Clamp(value, MIN_PARALLEL_BUILDS, MAX_PARALLEL_BUILDS));
    }

    // Main window tab tag shown at the next start; null = DS.
    public string? SelectedPage
    {
        get => _selectedPage;
        set => SetProperty(ref _selectedPage, Clean(value));
    }

    public static IReadOnlyList<int> ParallelBuildChoices { get; } =
        Enumerable.Range(MIN_PARALLEL_BUILDS, MAX_PARALLEL_BUILDS - MIN_PARALLEL_BUILDS + 1).ToArray();

    public static IReadOnlyList<string> IconBackgroundSwatches { get; } = [_WHITE, "#000000", "#222222"];

    public string RegistryPath => AppPaths.Registry;

    // Load or save problem; shown under the game list and on the Settings page until a save
    // succeeds.
    [ObservableProperty] private string? _storeError;

    // Hex box on the Settings page; invalid text is kept out of the setting and reported.
    public string IconBackgroundText
    {
        get => DefaultIconBackground ?? "";
        set
        {
            try
            {
                DefaultIconBackground = value;
                IconBackgroundError = null;
            }
            catch (ArgumentException error)
            {
                IconBackgroundError = error.Message;
            }
        }
    }

    [ObservableProperty] private string? _iconBackgroundError;
    [ObservableProperty] private string? _sdRomFolderError;

    // Null while checking.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GbaTemplateState))]
    private bool? _gbaTemplateInstalled;

    public string GbaTemplateState => TemplateState(GbaTemplateInstalled);

    // Null while checking.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GbTemplateState))]
    private bool? _gbTemplateInstalled;

    public string GbTemplateState => TemplateState(GbTemplateInstalled);

    // Null while checking.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NesTemplateState))]
    private bool? _nesTemplateInstalled;

    public string NesTemplateState => TemplateState(NesTemplateInstalled);

    // Card whose TWiLight Menu++ receivers the Settings row shows; null = no drive found.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReceiversText), nameof(ReceiversActionText))]
    [NotifyCanExecuteChangedFor(nameof(ReceiversActionCommand))]
    private SdCardInfo? _receiverCard;

    [ObservableProperty] private string? _receiversMessage;

    public string ReceiversText => ReceiverCard is { } card ? $"{card.Root} {card.Summary}"
        : "No SD card found";

    // Install when the card has no receivers yet, Update when any differs, Choose Drive without
    // a TWiLight Menu++ card.
    public string ReceiversActionText => ReceiverCard switch
    {
        { AllCurrent: true } => "Up to date",
        { HasTwilight: true } card when card.Receivers.Any(r => r.State != ReceiverState.Missing)
            => "Update",
        { HasTwilight: true } => "Install",
        _ => "Choose Drive",
    };

    [ObservableProperty] private string? _gbaTemplateMessage;
    [ObservableProperty] private string? _gbTemplateMessage;
    [ObservableProperty] private string? _nesTemplateMessage;
    [ObservableProperty] private string? _launchBoxDataState;
    [ObservableProperty] private string? _launchBoxStatus;
    [ObservableProperty] private double _launchBoxProgress;
    [ObservableProperty] private bool _launchBoxProgressUnknown;

    public static string? PickFolder(string title, string? initial)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (initial is not null && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    // Reads the receiver, GBA, GB/GBC and NES template and LaunchBox data state; the Settings
    // page calls it when shown.
    public async Task RefreshPageStateAsync()
    {
        await RefreshReceiversAsync();
        GbaTemplateInstalled = null;
        GbTemplateInstalled = null;
        NesTemplateInstalled = null;
        var refreshed = Path.Combine(AppPaths.LaunchBox, LaunchBoxIndex.FileName(GameSystem.Nds));
        try
        {
            var (gba, gb, nes, written) = await Task.Run(() => (
                GbaTemplate.IsInstalled(AppPaths.GbaTemplate),
                VcTemplate.IsInstalled(GameSystem.Gb, AppPaths.GbTemplate),
                VcTemplate.IsInstalled(GameSystem.Nes, AppPaths.NesTemplate),
                File.Exists(refreshed) ? File.GetLastWriteTime(refreshed) : (DateTime?)null));
            GbaTemplateInstalled = gba;
            GbTemplateInstalled = gb;
            NesTemplateInstalled = nes;
            LaunchBoxDataState = written is { } time ? $"Refreshed {time:yyyy-MM-dd HH:mm}"
                : "Bundled copy";
        }
        catch (Exception error)
        {
            GbaTemplateInstalled = false;
            GbTemplateInstalled = false;
            NesTemplateInstalled = false;
            GbaTemplateMessage = GbTemplateMessage = NesTemplateMessage = error.Message;
        }
    }

    private static string TemplateState(bool? installed) => installed switch
    {
        true => "Installed",
        false => "Not installed",
        null => "Checking",
    };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_storePath is not null && e.PropertyName is { } name && _PERSISTED.Contains(name))
            Save(_storePath);
    }

    [RelayCommand]
    private void BrowseCiaOutputFolder()
    {
        if (PickFolder("CIA Output Folder", CiaOutputFolder) is { } folder) CiaOutputFolder = folder;
    }

    [RelayCommand]
    private void BrowsePcDsRomFolder()
    {
        if (PickFolder("PC DS ROM Folder", PcDsRomFolder) is { } folder) PcDsRomFolder = folder;
    }

    [RelayCommand]
    private void BrowsePcGbaRomFolder()
    {
        if (PickFolder("PC GBA ROM Folder", PcGbaRomFolder) is { } folder) PcGbaRomFolder = folder;
    }

    [RelayCommand]
    private void BrowsePcGbRomFolder()
    {
        if (PickFolder("PC GB/GBC ROM Folder", PcGbRomFolder) is { } folder) PcGbRomFolder = folder;
    }

    [RelayCommand]
    private void BrowsePcNesRomFolder()
    {
        if (PickFolder("PC NES ROM Folder", PcNesRomFolder) is { } folder) PcNesRomFolder = folder;
    }

    [RelayCommand]
    private void PickSdCardFolder()
    {
        if (PickFolder("DS ROM Folder on the SD card", null) is not { } folder) return;
        try
        {
            SdRomFolder = SdPath.FromCardFolder(folder);
        }
        catch (ArgumentException error)
        {
            SdRomFolderError = error.Message;
        }
    }

    [RelayCommand]
    private void SetIconBackground(string color) => IconBackgroundText = color;

    // The chosen drive first, then the first removable drive with TWiLight Menu++, then one that
    // could not be read, then any.
    [RelayCommand]
    private async Task RefreshReceiversAsync()
    {
        var chosen = _chosenCardRoot;
        try
        {
            ReceiverCard = await Task.Run(() =>
            {
                var cards = SdCard.FindCards(AppPaths.SdFiles)
                    .Where(card => !SameRoot(card.Root, chosen)).ToList();
                if (chosen is not null) cards.Insert(0, SdCard.Inspect(chosen, AppPaths.SdFiles));
                return cards.FirstOrDefault(card => card.HasTwilight)
                    ?? cards.FirstOrDefault(card => card.Error is not null) ?? cards.FirstOrDefault();
            });
        }
        catch (Exception error)
        {
            ReceiverCard = null;
            ReceiversMessage = $"Could not read the SD cards: {error.Message}";
        }
    }

    // Copies the bundled receivers after a confirmation; SdCard.Install backs up and restores.
    // Without a TWiLight Menu++ card it asks for the drive instead, for card readers that
    // Windows reports as fixed disks. MainWindow stays open while this runs.
    [RelayCommand(CanExecute = nameof(CanRunReceiversAction))]
    private async Task ReceiversActionAsync()
    {
        if (ReceiverCard is not { HasTwilight: true, AllCurrent: false } card)
        {
            if (PickFolder("SD card drive", null) is not { } folder) return;
            var root = Path.GetPathRoot(folder);
            if (string.IsNullOrEmpty(root) || SdCard.IsSystemDrive(root))
            {
                ReceiversMessage = $"{root} is the system drive, not an SD card";
                return;
            }
            _chosenCardRoot = root;
            ReceiversMessage = null;
            await RefreshReceiversAsync();
            return;
        }

        var target = card.Root + SdCard.TWILIGHT_FOLDER;
        var replaced = card.Receivers.Where(r => r.State == ReceiverState.Different)
            .Select(r => r.FileName).ToList();
        var added = card.Receivers.Where(r => r.State == ReceiverState.Missing)
            .Select(r => r.FileName).ToList();
        var message = $"Copy the bundled receivers to {target}?";
        if (replaced.Count > 0)
            message += $"\n\nReplaces (backed up first): {string.Join(", ", replaced)}";
        if (added.Count > 0) message += $"\n\nAdds: {string.Join(", ", added)}";
        if (MessageBox.Show(Application.Current.MainWindow, message, "ciaGoober: SD card",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;

        ReceiversMessage = $"Copying the receivers to {card.Root}";
        try
        {
            var backup = await Task.Run(() =>
                SdCard.Install(card.Root, AppPaths.SdFiles, AppPaths.SdBackup));
            ReceiversMessage = backup is null ? $"Receivers installed on {card.Root}"
                : $"Receivers updated on {card.Root}; replaced files backed up to {backup}";
        }
        catch (Exception error)
        {
            ReceiversMessage = error.Message;
        }
        await RefreshReceiversAsync();
    }

    private bool CanRunReceiversAction() => ReceiverCard is not { AllCurrent: true };

    private static bool SameRoot(string a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task ImportGbaDonorAsync() =>
        await ImportDonorAsync("GBA", donor => GbaTemplate.ImportDonor(donor, AppPaths.GbaTemplate),
            message => GbaTemplateMessage = message);

    [RelayCommand]
    private async Task ImportGbDonorAsync() =>
        await ImportDonorAsync("Game Boy Color",
            donor => VcTemplate.ImportDonor(GameSystem.Gb, donor, AppPaths.GbTemplate),
            message => GbTemplateMessage = message);

    [RelayCommand]
    private async Task ImportNesDonorAsync() =>
        await ImportDonorAsync("NES",
            donor => VcTemplate.ImportDonor(GameSystem.Nes, donor, AppPaths.NesTemplate),
            message => NesTemplateMessage = message);

    // Asks for a decrypted VC title of the given system and imports it off the UI thread.
    private async Task ImportDonorAsync(string system, Action<string> import, Action<string> show)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Import {system} Donor CIA",
            Filter = $"Decrypted {system} Virtual Console title (*.cia;*.cxi;*.app)|*.cia;*.cxi;*.app",
        };
        if (dialog.ShowDialog() != true) return;
        var donor = dialog.FileName;
        show($"Importing {Path.GetFileName(donor)}");
        try
        {
            await Task.Run(() => import(donor));
            show($"Imported from {Path.GetFileName(donor)}");
        }
        catch (Exception error)
        {
            show($"Import failed: {error.Message}");
        }
        await RefreshPageStateAsync();
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RefreshLaunchBoxAsync(CancellationToken ct)
    {
        LaunchBoxProgress = 0;
        LaunchBoxProgressUnknown = true;
        LaunchBoxStatus = "Downloading LaunchBox data";
        // Created on the UI thread, so reports arrive on it.
        var progress = new Progress<LaunchBoxRefreshProgress>(ShowRefreshProgress);
        try
        {
            await Task.Run(() => LaunchBoxIndexBuilder.RefreshAsync(progress, ct), ct);
            LaunchBoxStatus = "LaunchBox data refreshed";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LaunchBoxStatus = "LaunchBox refresh cancelled";
        }
        catch (Exception error)
        {
            LaunchBoxStatus = $"LaunchBox refresh failed: {error.Message}";
        }
        LaunchBoxProgressUnknown = false;
        LaunchBoxProgress = 0;
        await RefreshPageStateAsync();
    }

    [RelayCommand]
    private void OpenRegistryFolder() =>
        Process.Start("explorer.exe", $"/select,\"{AppPaths.Registry}\"")?.Dispose();

    private void ShowRefreshProgress(LaunchBoxRefreshProgress report)
    {
        LaunchBoxProgressUnknown = report.Total <= 0;
        LaunchBoxProgress = report.Total > 0 ? (double)report.Done / report.Total : 0;
        var done = $"{report.Done / _MEGABYTE:0}";
        var total = $"{report.Total / _MEGABYTE:0}";
        LaunchBoxStatus = (report.Stage, report.Total > 0) switch
        {
            (LaunchBoxRefreshStage.Download, true) => $"Downloading LaunchBox data: {done} of {total} MB",
            (LaunchBoxRefreshStage.Download, false) => $"Downloading LaunchBox data: {done} MB",
            (_, true) => $"Indexing LaunchBox data: {LaunchBoxProgress:P0}",
            _ => "Indexing LaunchBox data",
        };
    }

    private void Save(string path)
    {
        try
        {
            SettingsStore.Save(path, new SettingsData(CiaOutputFolder, PcDsRomFolder,
                PcGbaRomFolder, PcGbRomFolder, SdRomFolder, DefaultIconBackground, ParallelBuilds,
                SelectedPage, PcNesRomFolder));
            StoreError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StoreError = $"Settings could not be saved to {path}: {error.Message}";
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
