using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CiaGoober.ViewModels;

// One Settings row: the preset ID that SNES games with this chip build with. Import reads it from
// a decrypted SNES VC title whose game uses the chip. UI thread only.
public partial class SnesChipPresetViewModel(SnesChip chip) : ObservableObject
{
    public SnesChip Chip { get; } = chip;
    public string Label => SnesRom.ChipLabel(Chip);

    // Hex text as typed and saved; empty = no preset.
    [ObservableProperty] private string _text = DefaultText(chip);

    // Invalid text or the outcome of the last import.
    [ObservableProperty] private string? _message;

    // The known preset of an official title with the chip, else empty.
    public static string DefaultText(SnesChip chip) =>
        SnesRom.KNOWN_PRESETS.TryGetValue(chip, out var preset) ? $"{preset:X4}" : "";

    // Null when empty; throws InvalidOperationException for text that is not a preset ID.
    public ushort? Preset => SnesRom.TryParsePreset(Text, out var preset) ? preset
        : throw new InvalidOperationException(
            $"The {Label} preset in Settings must be a hex number from 0000 to FFFF.");

    partial void OnTextChanged(string value) =>
        Message = SnesRom.TryParsePreset(value, out _) ? null : "Hex number from 0000 to FFFF";

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Import {Label} Preset",
            Filter = "Decrypted SNES Virtual Console title (*.cia;*.cxi;*.app)|*.cia;*.cxi;*.app",
        };
        if (dialog.ShowDialog() != true) return;
        var path = dialog.FileName;
        var name = Path.GetFileName(path);
        Message = $"Reading {name}";
        try
        {
            var donor = await Task.Run(() => VcTemplate.ReadSnesDonor(path));
            if (donor.Chip != Chip)
            {
                Message = $"{name} uses {SnesRom.ChipName(donor.Chip)}, not "
                    + SnesRom.ChipName(Chip);
                return;
            }
            Text = $"{donor.Preset:X4}";
            Message = $"Imported from {name}";
        }
        catch (Exception error)
        {
            Message = $"Import failed: {error.Message}";
        }
    }
}
