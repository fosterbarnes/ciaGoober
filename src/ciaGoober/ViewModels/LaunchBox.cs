using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CiaGoober.Helpers;
using CiaGoober.Windows;

namespace CiaGoober.ViewModels;

// One image type tab; Type is null for All. A class, so every game gets new tabs.
public sealed class LaunchBoxTab(string? type, int count)
{
    public string? Type { get; } = type;
    public int Count { get; } = count;
    public string Label => $"{Type ?? "All"} ({Count})";
}

// One image in the browser grid. A loaded thumbnail stays across tab and region changes.
public sealed partial class LaunchBoxTile(LaunchBoxImage image) : ObservableObject
{
    public LaunchBoxImage Image { get; } = image;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShapeTarget))]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Failed), nameof(Description))]
    private string? _error;

    public bool Failed => Error is not null;

    public string Description => Error is null ? $"{Image.Type}, {Image.Region}"
        : $"{Image.Type}, {Image.Region}: thumbnail failed ({Error})";

    // Roughly square images default to the HOME Menu icon, others to the banner (nds
    // _shapeTarget). The thumbnail keeps the original's shape; unknown until it loads.
    public CropTarget? ShapeTarget => Thumbnail is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } bitmap
        ? (double)bitmap.PixelWidth / bitmap.PixelHeight is >= 0.8 and <= 1.25
            ? CropTarget.Icon : CropTarget.Banner
        : null;

    // Cancellation propagates; any other failure becomes the tile's error.
    internal async Task LoadThumbnailAsync(string thumbFolder, string imageFolder,
        CancellationToken ct)
    {
        var file = Image.File;
        try
        {
            Thumbnail = await Task.Run(async () => PngImage.Load(await File.ReadAllBytesAsync(
                await LaunchBoxDownloads.ThumbnailAsync(file, thumbFolder, imageFolder, ct), ct)), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Error = ex.Message;
        }
    }
}

// The LaunchBox image browser for one card, opened for its banner or icon (nds openPicker).
// Create and use it on the UI thread; Close stops its downloads.
public sealed partial class LaunchBoxViewModel : ObservableObject
{
    public const string MISSING_DATA = "LaunchBox data is missing: Settings > Refresh LaunchBox Data";
    private const string _ALL_REGIONS = "All regions";
    private const int _RESULT_LIMIT = 12;
    private const int _PARALLEL_THUMBNAILS = 4;

    private readonly string _imageFolder, _thumbFolder;
    // This game's tiles by image, so a tab shown again keeps its thumbnails and failures.
    private readonly Dictionary<LaunchBoxImage, LaunchBoxTile> _tiles = [];
    private IReadOnlyList<LaunchBoxImage> _tabImages = [];
    private LaunchBoxIndex? _index;
    // A newer request cancels the older one, so stale results never land.
    private CancellationTokenSource? _thumbnails, _selection, _iconRender;
    private Task<string?> _selectionTask = Task.FromResult<string?>(null);
    private bool _closed;

    // Full images and thumbnails are cached in these folders (AppPaths in the app).
    public LaunchBoxViewModel(GameViewModel game, CropTarget target, string imageFolder,
        string thumbFolder)
    {
        Game = game;
        Target = target;
        _imageFolder = imageFolder;
        _thumbFolder = thumbFolder;
        game.PropertyChanged += OnGamePropertyChanged;
    }

    public GameViewModel Game { get; }
    // The card row the browser was opened from.
    public CropTarget Target { get; }

    // Raised after the card's banner or icon was set.
    public event EventHandler? Applied;

    [ObservableProperty] private bool _isIndexed;
    [ObservableProperty] private string? _stateText;
    [ObservableProperty] private string _query = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    private IReadOnlyList<LaunchBoxGame> _results = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenOnLaunchBoxCommand))]
    private LaunchBoxGame? _currentGame;

    [ObservableProperty] private IReadOnlyList<LaunchBoxTab> _tabs = [];
    [ObservableProperty] private LaunchBoxTab? _selectedTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRegions))]
    private IReadOnlyList<string> _regions = [];

    [ObservableProperty] private string? _selectedRegion;
    [ObservableProperty] private IReadOnlyList<LaunchBoxTile> _visibleTiles = [];
    [ObservableProperty] private LaunchBoxTile? _selectedTile;

    [ObservableProperty] private bool _isFetchingThumbnails;
    [ObservableProperty] private int _thumbnailsDone;
    [ObservableProperty] private int _thumbnailsTotal;
    [ObservableProperty] private string? _thumbnailText;

    // The selected tile's downloaded full image; null until it is ready.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseBannerCommand), nameof(UseIconCommand),
        nameof(CropCommand))]
    private string? _selectedPath;

    [ObservableProperty] private ImageSource? _selectedBannerPreview;
    [ObservableProperty] private ImageSource? _selectedIconPreview;
    [ObservableProperty] private string? _selectionText;
    [ObservableProperty] private string? _selectionStatus;
    [ObservableProperty] private string? _selectionError;

    public bool HasResults => Results.Count > 0;
    public bool HasRegions => Regions.Count > 0;

    // Loads the index and match off the UI thread; without a match, searches the ROM's title.
    public async Task LoadAsync()
    {
        StateText = "Loading LaunchBox data";
        var rom = Game.Rom;
        LaunchBoxIndex? index;
        LaunchBoxGame? match;
        try
        {
            (index, match) = await Task.Run(() =>
            {
                var loaded = LaunchBoxIndex.Load(rom.System);
                return (loaded, loaded is null ? null : LaunchBoxMatcher.Match(loaded, rom));
            });
        }
        catch (Exception ex)
        {
            StateText = $"LaunchBox data could not be read: {ex.Message}";
            return;
        }
        if (_closed) return;
        _index = index;
        IsIndexed = index is not null;
        if (index is null)
        {
            StateText = MISSING_DATA;
            return;
        }
        if (match is not null)
        {
            ShowGame(match);
            return;
        }
        Query = SearchTitle(rom);
        StateText = HasResults ? "No exact LaunchBox match: pick a game"
            : "No LaunchBox match: search by name";
    }

    // Opens on the tab the automatic pick for this row would use (nds showPickerGame).
    public void ShowGame(LaunchBoxGame game)
    {
        StopThumbnails();
        _tiles.Clear();
        Query = "";
        CurrentGame = game;
        var types = LaunchBoxImages.Types(game);
        StateText = types.Count == 0 ? "No LaunchBox images for this game" : null;
        Tabs = types.Count == 0 ? []
            : [.. types.Select(type => new LaunchBoxTab(type.Type, type.Count)),
                new LaunchBoxTab(null, game.Images.Count)];
        var first = LaunchBoxImages.DefaultType(game, Target == CropTarget.Icon, Game.System);
        var tab = Tabs.FirstOrDefault(entry => entry.Type == first);
        if (SelectedTab == tab) ShowTab(tab);
        else SelectedTab = tab;
    }

    // Enter or double-click: applies the tile to its shape's target once its image is ready.
    public async Task ApplyDefaultAsync(LaunchBoxTile tile)
    {
        if (tile.ShapeTarget is not { } target) return;
        SelectedTile = tile;
        var path = await _selectionTask;
        if (path is not null && SelectedTile == tile) Apply(target, null);
    }

    // Sets the card's banner or icon to the selected full image.
    public void Apply(CropTarget target, CropRect? crop)
    {
        if (_closed || SelectedPath is not { } path) return;
        Game.SetArt(target, new ArtChoice(path, crop));
        Applied?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        _closed = true;
        Game.PropertyChanged -= OnGamePropertyChanged;
        _thumbnails?.Cancel();
        _selection?.Cancel();
        _iconRender?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void UseBanner() => Apply(CropTarget.Banner, null);

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void UseIcon() => Apply(CropTarget.Icon, null);

    // The crop window opens on the shape's target and may switch it; its target is applied.
    [RelayCommand(CanExecute = nameof(HasImage))]
    private void Crop(Window owner)
    {
        if (SelectedPath is not { } path) return;
        if (CropWindow.Pick(owner, path, SelectedTile?.ShapeTarget ?? Target, null,
            Game.EffectiveIconBackground, true) is { } result)
            Apply(result.Target, result.Crop);
    }

    private bool HasImage() => SelectedPath is not null;

    [RelayCommand(CanExecute = nameof(HasGame))]
    private void OpenOnLaunchBox()
    {
        if (CurrentGame is not { } game) return;
        Process.Start(new ProcessStartInfo(LaunchBoxImages.SiteUrl(game)) { UseShellExecute = true })
            ?.Dispose();
    }

    private bool HasGame() => CurrentGame is not null;

    partial void OnQueryChanged(string value) =>
        Results = _index?.Search(value, _RESULT_LIMIT) ?? [];

    partial void OnSelectedTabChanged(LaunchBoxTab? value) => ShowTab(value);

    partial void OnSelectedRegionChanged(string? value) => ShowTiles();

    partial void OnSelectedTileChanged(LaunchBoxTile? value) =>
        _selectionTask = LoadSelectionAsync(value);

    private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameViewModel.EffectiveIconBackground))
            _ = RenderSelectedIconAsync();
    }

    private void ShowTab(LaunchBoxTab? tab)
    {
        SelectedTile = null;
        _tabImages = tab is null || CurrentGame is null ? []
            : LaunchBoxImages.OfType(CurrentGame, tab.Type);
        var regions = LaunchBoxImages.Regions(_tabImages);
        Regions = regions.Count == 0 ? [] : [_ALL_REGIONS, .. regions];
        SelectedRegion = HasRegions ? _ALL_REGIONS : null;
        ShowTiles();
    }

    // The open tab's images in the chosen region; fetches their missing thumbnails. List
    // controls write selections back while their items change, so repeats are skipped.
    private void ShowTiles()
    {
        var region = SelectedRegion is null or _ALL_REGIONS ? null : SelectedRegion;
        LaunchBoxTile[] tiles =
            [.. _tabImages.Where(image => region is null || image.Region == region).Select(Tile)];
        if (tiles.SequenceEqual(VisibleTiles)) return;
        if (SelectedTile is not null && !tiles.Contains(SelectedTile)) SelectedTile = null;
        VisibleTiles = tiles;
        _ = FetchThumbnailsAsync(tiles);
    }

    private LaunchBoxTile Tile(LaunchBoxImage image) =>
        _tiles.TryGetValue(image, out var tile) ? tile : _tiles[image] = new LaunchBoxTile(image);

    // A few at a time in grid order. Finished thumbnails stay on disk and on their tiles.
    private async Task FetchThumbnailsAsync(IReadOnlyList<LaunchBoxTile> tiles)
    {
        var ct = Restart(ref _thumbnails);
        var missing = new Queue<LaunchBoxTile>(
            tiles.Where(tile => tile.Thumbnail is null && !tile.Failed));
        int total = missing.Count, done = 0, failed = 0;
        string? lastError = null;
        ThumbnailsTotal = total;
        ThumbnailsDone = 0;
        IsFetchingThumbnails = total > 0;
        ThumbnailText = total > 0 ? $"Thumbnails 0/{total}" : null;

        async Task Work()
        {
            while (missing.TryDequeue(out var tile))
            {
                try
                {
                    await tile.LoadThumbnailAsync(_thumbFolder, _imageFolder, ct);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    return;
                }
                if (ct.IsCancellationRequested) return;
                if (tile.Error is { } error)
                {
                    failed++;
                    lastError = error;
                }
                ThumbnailsDone = ++done;
                ThumbnailText = $"Thumbnails {done}/{total}";
            }
        }

        await Task.WhenAll(Enumerable.Range(0, _PARALLEL_THUMBNAILS).Select(_ => Work()));
        if (ct.IsCancellationRequested) return;
        IsFetchingThumbnails = false;
        ThumbnailText = failed == 0 ? null
            : $"{failed} of {total} thumbnails could not be downloaded: {lastError}";
    }

    private void StopThumbnails()
    {
        _thumbnails?.Cancel();
        IsFetchingThumbnails = false;
        ThumbnailText = null;
    }

    // Downloads the full image and renders the exact build previews. Never faults.
    private async Task<string?> LoadSelectionAsync(LaunchBoxTile? tile)
    {
        var ct = Restart(ref _selection);
        _iconRender?.Cancel();
        SelectedPath = null;
        SelectedBannerPreview = null;
        SelectedIconPreview = null;
        SelectionError = null;
        SelectionText = tile is null ? null : $"{tile.Image.Type}, {tile.Image.Region}";
        SelectionStatus = tile is null ? null : "Downloading image";
        if (tile is null) return null;
        var file = tile.Image.File;
        var folder = _imageFolder;
        try
        {
            var path = await Task.Run(() => LaunchBoxDownloads.ImageAsync(file, folder, ct), ct);
            var banner = await Task.Run(() => PngImage.Load(ArtImages.RenderBanner(path, null)), ct);
            if (ct.IsCancellationRequested) return null;
            SelectedPath = path;
            SelectedBannerPreview = banner;
            SelectionStatus = null;
            _ = RenderSelectedIconAsync();
            return path;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            SelectionStatus = null;
            SelectionError = $"Could not load the image: {ex.Message}";
            return null;
        }
    }

    // The 48x48 preview on the card's icon background, again when that background changes.
    private async Task RenderSelectedIconAsync()
    {
        if (SelectedPath is not { } path) return;
        var ct = Restart(ref _iconRender);
        var background = Game.EffectiveIconBackground;
        try
        {
            var icon = await Task.Run(
                () => PngImage.Load(ArtImages.RenderIcon(path, null, background)), ct);
            if (!ct.IsCancellationRequested) SelectedIconPreview = icon;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SelectionError = $"Could not render the icon: {ex.Message}";
        }
    }

    // The ROM's title, else its file name without "(USA)"-style tags (nds loadPickerIndex).
    private static string SearchTitle(RomInfo rom)
    {
        var title = LaunchBoxMatcher.CleanTitle(rom.TitleLines[0]);
        if (title.Length > 0) return title;
        var stem = Path.GetFileNameWithoutExtension(rom.Path);
        var tags = stem.IndexOf('(');
        return (tags < 0 ? stem : stem[..tags]).Trim();
    }

    private static CancellationToken Restart(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source = new CancellationTokenSource();
        return source.Token;
    }
}
