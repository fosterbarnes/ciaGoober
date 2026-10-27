using System.Text;

namespace CiaGoober.Core;

// Nintendo's NES/GB/GBC Virtual Console emulator (one build for all three), taken from an
// official VC title the user owns: the signed exheader, the emulator (.code), the boot logo and
// the RomFS without the game and its patches. RomName is the game's RomFS path, since the
// emulator loads the one file in /rom/. GB and GBC use a GBC donor, NES an NES donor, each in its
// own folder. Nothing of it is bundled or downloaded.
public sealed record VcTemplate(byte[] Exheader, byte[] Code, byte[] Logo,
    IReadOnlyDictionary<string, byte[]> Files, string RomName)
{
    private static readonly string[] _FILES =
        ["exheader.bin", "code.bin", "logo.bin", "romfs.bin", "romName.txt"];
    private const string _CONFIG = "/config.ini";
    private const string _ROM_FOLDER = "/rom/";
    // Parallel builds read the folder that an import replaces.
    private static readonly SemaphoreSlim _GATE = new(1, 1);

    public static bool IsInstalled(GameSystem system, string directory)
    {
        try
        {
            Load(system, directory);
            return true;
        }
        catch (VcTemplateMissingException)
        {
            return false;
        }
    }

    // Throws VcTemplateMissingException when missing or damaged.
    public static VcTemplate Load(GameSystem system, string directory)
    {
        _GATE.Wait();
        try
        {
            return Read(system, directory);
        }
        finally
        {
            _GATE.Release();
        }
    }

    // Extracts the template from a decrypted GBC or NES VC .cia, .cxi or .app.
    public static void ImportDonor(GameSystem system, string donorPath, string directory)
    {
        var parts = Ncch.ParseTitle(donorPath);
        var files = parts.RomFs.Length > 0 ? RomFs.Read(parts.RomFs) : [];
        var games = files.Keys.Where(name => name.StartsWith(_ROM_FOLDER, StringComparison.Ordinal))
            .ToList();
        if (!files.ContainsKey(_CONFIG) || games.Count != 1
            || !parts.ExefsFiles.TryGetValue(".code", out var code))
            throw new InvalidDataException($"Not {Article(system)} Virtual Console title (no /rom/ "
                + "and config.ini in its RomFS).");
        CheckDonorGame(system, files[games[0]]);
        var kept = files.Where(file => !file.Key.StartsWith(_ROM_FOLDER, StringComparison.Ordinal)
            && !file.Key.EndsWith(".patch", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(StringComparer.Ordinal);
        var logo = parts.ExefsFiles.GetValueOrDefault("logo") is { Length: > 0 } exefsLogo
            ? exefsLogo : parts.LogoRegion;
        var template = new VcTemplate(parts.Exheader, code, logo, kept, games[0]);
        if (template.Problem() is { } problem)
            throw new InvalidDataException($"Template rejected: {problem}.");
        _GATE.Wait();
        try
        {
            TemplateFolder.Replace(directory, _FILES.Zip((byte[][])[template.Exheader, code, logo,
                RomFs.Build(kept), Encoding.UTF8.GetBytes(template.RomName)]));
        }
        finally
        {
            _GATE.Release();
        }
    }

    private static void CheckDonorGame(GameSystem system, byte[] game)
    {
        switch (system)
        {
            case GameSystem.Gb:
                if (game.Length < GbRom.MIN_ROM_SIZE || !GbRom.HeaderValid(game))
                    throw new InvalidDataException("The donor's game is not a Game Boy ROM.");
                if (!GbRom.IsColor(game))
                    throw new InvalidDataException("This is a Game Boy title; use a Game Boy Color "
                        + "Virtual Console title, whose emulator also runs Game Boy games.");
                break;
            case GameSystem.Nes:
                if (!NesRom.IsTnes(game))
                    throw new InvalidDataException(
                        "The donor's game is not an NES Virtual Console ROM.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(system), system, null);
        }
    }

    private static VcTemplate Read(GameSystem system, string directory)
    {
        if (!_FILES.All(name => File.Exists(Path.Combine(directory, name))))
            throw new VcTemplateMissingException(
                $"{Label(system)} template is not installed in {directory}. {DonorHint(system)}");
        var parts = _FILES.Select(name => File.ReadAllBytes(Path.Combine(directory, name))).ToArray();
        VcTemplate template;
        try
        {
            template = new VcTemplate(parts[0], parts[1], parts[2], RomFs.Read(parts[3]),
                Encoding.UTF8.GetString(parts[4]));
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException)
        {
            throw new VcTemplateMissingException($"{Label(system)} template in {directory} is "
                + $"damaged ({error.Message}). {DonorHint(system)}");
        }
        if (template.Problem() is { } problem)
            throw new VcTemplateMissingException($"{Label(system)} template in {directory} is "
                + $"damaged ({problem}). {DonorHint(system)}");
        return template;
    }

    private static string Label(GameSystem system) => system switch
    {
        GameSystem.Gb => "GB/GBC",
        GameSystem.Nes => "NES",
        _ => throw new ArgumentOutOfRangeException(nameof(system), system, null),
    };

    private static string Article(GameSystem system) => system switch
    {
        GameSystem.Gb => "a Game Boy",
        GameSystem.Nes => "an NES",
        _ => throw new ArgumentOutOfRangeException(nameof(system), system, null),
    };

    private static string DonorHint(GameSystem system) => system switch
    {
        GameSystem.Gb => "Import a decrypted Game Boy Color Virtual Console title in Settings > "
            + "Import GB/GBC Donor CIA",
        GameSystem.Nes => "Import a decrypted NES Virtual Console title in Settings > Import NES "
            + "Donor CIA",
        _ => throw new ArgumentOutOfRangeException(nameof(system), system, null),
    } + " (GodMode9: Title manager -> the game -> Build CIA (standard)).";

    private string? Problem() =>
        Exheader.Length != Ncch.EXHEADER_FULL_SIZE ? "exheader has the wrong size"
        : Code.Length == 0 ? "emulator code is empty"
        : Logo.Length == 0 ? "boot logo is empty"
        : !Files.ContainsKey(_CONFIG) ? "RomFS has no config.ini"
        : !RomName.StartsWith(_ROM_FOLDER, StringComparison.Ordinal)
            || RomName.Length == _ROM_FOLDER.Length ? "ROM path is not in /rom/"
        : null;
}

public sealed class VcTemplateMissingException(string message) : Exception(message);
