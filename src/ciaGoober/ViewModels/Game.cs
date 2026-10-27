using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using CiaGoober.Helpers;
using CiaGoober.Windows;

namespace CiaGoober.ViewModels;

public enum GameStatus { Idle, Queued, Building, Built, Failed }

public sealed record IconSwatch(string Name, string Color);

// One game card. Lives only for this app run. Create and use it on the UI thread.
public partial class GameViewModel : ObservableObject
{
    private const string _IMAGE_FILTER =
        "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp";
    private const string _SOUND_FILTER = "Sounds (*.wav;*.ogg)|*.wav;*.ogg";
    private static readonly TimeSpan _PREVIEW_DELAY = TimeSpan.FromMilliseconds(150);

    // A newer preview request cancels the older one, so a stale render never lands.
    private CancellationTokenSource? _bannerRender, _iconRender;

    public GameViewModel(RomInfo rom, SettingsViewModel settings)
    {
        Rom = rom;
        Settings = settings;
        _titleShort = RomTitle(0);
        _titleLong = RomTitle(1);
        _titlePublisher = RomTitle(2);
        // Weak, so a card removed from the list is not kept alive by the settings.
        PropertyChangedEventManager.AddHandler(settings, OnSettingsChanged, "");
        RefreshSdPath();
        QueuePreviews();
    }

    public static IReadOnlyList<IconSwatch> IconSwatches { get; } =
        [new("White", "#FFFFFF"), new("Black", "#000000"), new("Dark gray", "#222222")];

    public RomInfo Rom { get; }
    public SettingsViewModel Settings { get; }
    public string RomPath => Rom.Path;
    public GameSystem System => Rom.System;
    public bool IsDs => System == GameSystem.Nds;
    public bool IsGba => System == GameSystem.Gba;
    public string SystemLabel => Label(System);

    public static string Label(GameSystem system) => system switch
    {
        GameSystem.Nds => "DS",
        GameSystem.Gba => "GBA",
        _ => "GB/GBC",
    };
    public string GameCode => Rom.GameCode;
    public string DisplayName => Path.GetFileNameWithoutExtension(Rom.Path);

    [ObservableProperty] private GameStatus _status;
    [ObservableProperty] private string? _statusText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputCommand))]
    private string? _outputPath;

    [ObservableProperty] private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannerSourceText), nameof(IconSourceText))]
    [NotifyCanExecuteChangedFor(nameof(CropCommand))]
    private ArtChoice _banner = new(null, null);

    // Null Source: DS uses the ROM's own icon, GBA and GB use the banner.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannerSourceText), nameof(IconSourceText))]
    [NotifyCanExecuteChangedFor(nameof(CropCommand))]
    private ArtChoice _icon = new(null, null);

    // Explicit "Use DS icon" choice; counts as chosen for auto-fill.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconSourceText))]
    [NotifyCanExecuteChangedFor(nameof(CropCommand))]
    private bool _useRomIcon;

    // "#RRGGBB"; null = Settings default.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveIconBackground))]
    private string? _iconBackground;

    // Null = bundled per-system sound (ForwarderBuilder.DefaultSoundPath).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SoundText))]
    [NotifyCanExecuteChangedFor(nameof(ResetSoundCommand))]
    private string? _soundPath;

    [ObservableProperty] private string _titleShort;
    [ObservableProperty] private string _titleLong;
    [ObservableProperty] private string _titlePublisher;

    // DS only: typed SD path that overrides the resolved one.
    [ObservableProperty] private string? _customSdPath;

    // DS only: the path the CIA will launch, or why there is none.
    [ObservableProperty] private string? _resolvedSdPath;
    [ObservableProperty] private string? _sdPathError;

    // GBA only.
    [ObservableProperty] private string _saveType = GbaChoices.DEFAULT_SAVE_TYPE;
    [ObservableProperty] private string _screen = GbaChoices.DEFAULT_SCREEN;
    [ObservableProperty] private string _ghosting = GbaChoices.DEFAULT_GHOSTING;

    [ObservableProperty] private ImageSource? _bannerPreview;
    [ObservableProperty] private ImageSource? _iconPreview;
    [ObservableProperty] private string? _bannerPreviewError;
    [ObservableProperty] private string? _iconPreviewError;

    // Outcome of the card's Auto-Fill Artwork button.
    [ObservableProperty] private string? _autoFillText;

    // Bound by the swatches and the hex box; setting it stores this card's own color.
    public string EffectiveIconBackground
    {
        get => IconBackground ?? Settings.EffectiveIconBackground;
        set
        {
            // A ListBox clears its selected value when the color is not a swatch.
            if (value is not null) IconBackground = IconColor.Normalize(value.Trim());
        }
    }

    public string BannerSourceText => Banner.Source is { } source ? Path.GetFileName(source)
        : IsDs ? "Downloaded at build"
        : Icon.Source is null ? "None" : "Icon image";

    public string IconSourceText => IsDs && (UseRomIcon || Icon.Source is null) ? "DS icon"
        : Icon.Source is { } source ? Path.GetFileName(source)
        : Banner.Source is null ? "None" : "Banner image";

    public string SoundText => SoundPath is { } path ? Path.GetFileName(path)
        : (IsDs ? "Downloaded at build, else " : "")
            + Path.GetFileName(ForwarderBuilder.DefaultSoundPath(System));

    // Snapshot for BuildQueue; throws with a user-facing message when the card cannot build.
    public BuildJob ToBuildJob()
    {
        var customSdPath = string.IsNullOrWhiteSpace(CustomSdPath) ? null : CustomSdPath;
        if (IsDs) SdPath.Resolve(RomPath, customSdPath, Settings.SdRomFolder);
        if (!IsDs && Banner.Source is null && Icon.Source is null)
            throw new InvalidOperationException(
                $"{SystemLabel} games need a banner or icon image: choose a file or use Auto-Fill "
                + "Artwork.");
        if (string.IsNullOrWhiteSpace(TitleShort))
            throw new InvalidOperationException("The short title is empty.");
        return new BuildJob(RomPath, System, IsDs ? customSdPath : null, Banner, Icon,
            IsDs && UseRomIcon, EffectiveIconBackground, SoundPath,
            [TitleShort, TitleLong, TitlePublisher],
            IsGba ? new GbaOptions(SaveType, Screen, Ghosting) : null,
            Settings.CiaOutputFolder, Settings.SdRomFolder);
    }

    // Fills only unchosen art unless replace. A choice made while the lookup runs wins.
    public async Task<LaunchBoxArtResult> AutoFillArtworkAsync(bool replace, CancellationToken ct)
    {
        var banner = Banner;
        var icon = Icon;
        var useRomIcon = UseRomIcon;
        var wantBanner = replace || banner.Source is null;
        var wantIcon = replace || (icon.Source is null && !useRomIcon);
        var rom = Rom;
        var result = await Task.Run(
            () => LaunchBoxArt.AutoFillAsync(rom, wantBanner, wantIcon, ct), ct);
        if (result.BannerPath is { } bannerPath && Banner == banner) Banner = new(bannerPath, null);
        if (result.IconPath is { } iconPath && Icon == icon && UseRomIcon == useRomIcon)
        {
            Icon = new(iconPath, null);
            UseRomIcon = false;
        }
        return result;
    }

    [RelayCommand]
    private void ChooseBanner()
    {
        if (PickFile("Choose banner image", _IMAGE_FILTER, Banner.Source) is { } path)
            Banner = new(path, null);
    }

    [RelayCommand]
    private void ChooseIcon()
    {
        if (PickFile("Choose HOME Menu icon image", _IMAGE_FILTER, Icon.Source) is not { } path)
            return;
        Icon = new(path, null);
        UseRomIcon = false;
    }

    [RelayCommand]
    private void UseDsIcon()
    {
        Icon = new(null, null);
        UseRomIcon = true;
    }

    // Sets a chosen banner or icon image; an icon image replaces the DS icon choice.
    public void SetArt(CropTarget target, ArtChoice art)
    {
        if (target == CropTarget.Banner)
        {
            Banner = art;
            return;
        }
        Icon = art;
        UseRomIcon = false;
    }

    [RelayCommand]
    private void OpenLaunchBox(CropTarget target) =>
        LaunchBoxWindow.Open(Application.Current.MainWindow, this, target);

    [RelayCommand(CanExecute = nameof(CanCrop))]
    private void Crop(CropTarget target)
    {
        if (CropStart(target) is not { } start) return;
        if (CropWindow.Pick(Application.Current.MainWindow, start.Source, target, start.Crop,
            EffectiveIconBackground, false) is { } result)
            SetArt(result.Target, new(start.Source, result.Crop));
    }

    private bool CanCrop(CropTarget target) => CropStart(target) is not null;

    // The image a crop edits, as ForwarderBuilder picks it: a GBA or GB card without that image
    // uses the other one, whose crop does not carry over. Null for the DS ROM icon or no image.
    private (string Source, CropRect? Crop)? CropStart(CropTarget target)
    {
        var icon = target == CropTarget.Icon;
        if (icon && IsDs && UseRomIcon) return null;
        var (own, other) = icon ? (Icon, Banner) : (Banner, Icon);
        if (own.Source is { } source) return (source, own.Crop);
        return !IsDs && other.Source is { } fallback ? (fallback, null) : null;
    }

    [RelayCommand]
    private void ChooseSound()
    {
        if (PickFile("Choose banner sound", _SOUND_FILTER, SoundPath) is { } path) SoundPath = path;
    }

    [RelayCommand(CanExecute = nameof(HasSound))]
    private void ResetSound() => SoundPath = null;

    private bool HasSound() => SoundPath is not null;

    [RelayCommand]
    private void ResetTitle()
    {
        TitleShort = RomTitle(0);
        TitleLong = RomTitle(1);
        TitlePublisher = RomTitle(2);
    }

    // The card button replaces both images, like nds autoFillArtwork.
    [RelayCommand]
    private async Task RunAutoFillAsync(CancellationToken ct)
    {
        AutoFillText = "Searching LaunchBox";
        try
        {
            var result = await AutoFillArtworkAsync(true, ct);
            AutoFillText = !result.Indexed
                ? "LaunchBox data is missing: Settings > Refresh LaunchBox Data"
                : result.Game is null ? "No LaunchBox match"
                : $"Banner {Outcome(result.BannerPath, result.BannerError)}, "
                    + $"icon {Outcome(result.IconPath, result.IconError)}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AutoFillText = null;
        }
        catch (Exception ex)
        {
            AutoFillText = $"Auto-Fill failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasOutput))]
    private void OpenOutput() =>
        Process.Start("explorer.exe", $"/select,\"{OutputPath}\"")?.Dispose();

    private bool HasOutput() => OutputPath is not null;

    partial void OnBannerChanged(ArtChoice value) => QueuePreviews();
    partial void OnIconChanged(ArtChoice value) => QueuePreviews();
    partial void OnUseRomIconChanged(bool value) => QueueIconPreview();
    partial void OnIconBackgroundChanged(string? value) => QueueIconPreview();
    partial void OnCustomSdPathChanged(string? value) => RefreshSdPath();

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.SdRomFolder):
                RefreshSdPath();
                break;
            case nameof(SettingsViewModel.EffectiveIconBackground) when IconBackground is null:
                OnPropertyChanged(nameof(EffectiveIconBackground));
                QueueIconPreview();
                break;
        }
    }

    private string RomTitle(int line) => Rom.TitleLines.ElementAtOrDefault(line) ?? "";

    private void RefreshSdPath()
    {
        if (!IsDs) return;
        try
        {
            ResolvedSdPath = SdPath.Resolve(RomPath, CustomSdPath, Settings.SdRomFolder);
            SdPathError = null;
        }
        catch (ArgumentException ex)
        {
            ResolvedSdPath = null;
            SdPathError = ex.Message;
        }
    }

    private void QueuePreviews()
    {
        QueueBannerPreview();
        QueueIconPreview();
    }

    // Same art choice as ForwarderBuilder: a GBA or GB card without a banner uses its icon image.
    private void QueueBannerPreview()
    {
        var art = !IsDs && Banner.Source is null ? Icon : Banner;
        Func<byte[]?> render = art.Source is { } source
            ? () => ArtImages.RenderBanner(source, art.Crop)
            : () => null;
        _ = RenderPreviewAsync(Restart(ref _bannerRender), render, (image, error) =>
        {
            BannerPreview = image;
            BannerPreviewError = error;
        });
    }

    // Same art choice as ForwarderBuilder: DS falls back to the ROM icon, GBA and GB to the banner.
    private void QueueIconPreview()
    {
        var background = EffectiveIconBackground;
        var romPath = RomPath;
        var art = Icon.Source is null ? Banner : Icon;
        Func<byte[]?> render = IsDs && (UseRomIcon || Icon.Source is null)
            ? () => ArtImages.RenderRomIcon(NdsRom.Icon(romPath), background)
            : art.Source is { } source
                ? () => ArtImages.RenderIcon(source, art.Crop, background)
                : () => null;
        _ = RenderPreviewAsync(Restart(ref _iconRender), render, (image, error) =>
        {
            IconPreview = image;
            IconPreviewError = error;
        });
    }

    private static CancellationToken Restart(ref CancellationTokenSource? render)
    {
        render?.Cancel();
        render = new CancellationTokenSource();
        return render.Token;
    }

    // Never faults: a render error becomes card state. Continues on the UI thread, where every
    // cancel also happens, so the final check cannot race a newer request.
    private static async Task RenderPreviewAsync(CancellationToken ct, Func<byte[]?> render,
        Action<ImageSource?, string?> show)
    {
        ImageSource? image = null;
        string? error = null;
        try
        {
            await Task.Delay(_PREVIEW_DELAY, ct);
            image = await Task.Run(() => render() is { } png ? PngImage.Load(png) : null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        if (!ct.IsCancellationRequested) show(image, error);
    }

    private static string Outcome(string? path, Exception? error) =>
        path is not null ? "set" : error is not null ? $"download failed ({error.Message})"
            : "no match";

    private static string? PickFile(string title, string filter, string? current)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        if (current is not null) dialog.InitialDirectory = Path.GetDirectoryName(current);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
