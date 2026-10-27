using System.Text;

namespace CiaGoober.Core;

// A Nintendo Virtual Console emulator taken from an official VC title the user owns: the signed
// exheader, the emulator (.code), the boot logo and the RomFS without the game. RomName is the
// game's RomFS path. GB, GBC and NES share one emulator, which loads the one file in /rom/ (GB and
// GBC use a GBC donor, NES an NES donor). SNES uses the New 3DS SNES emulator, which loads
// /data.bin; SnesHeader keeps that file's 0x60 header. Each system has its own folder. Nothing
// of it is bundled or downloaded.
public sealed record VcTemplate(byte[] Exheader, byte[] Code, byte[] Logo,
    IReadOnlyDictionary<string, byte[]> Files, string RomName, byte[]? SnesHeader = null)
{
    private static readonly string[] _FILES =
        ["exheader.bin", "code.bin", "logo.bin", "romfs.bin", "romName.txt"];
    private static readonly string[] _SNES_FILES = ["gameHeader.bin"];
    private const string _CONFIG = "/config.ini";
    private const string _ROM_FOLDER = "/rom/";
    private const string _SNES_DATA = "/data.bin";
    // Parallel builds read the folder that an import replaces.
    private static readonly SemaphoreSlim _GATE = new(1, 1);

    // The SNES title's copy of its SMDH (/KTR-XXXX.icn), replaced by each game's own.
    public string? SnesIcon => Files.Keys.Where(name => name.LastIndexOf('/') == 0
        && name.EndsWith(".icn", StringComparison.OrdinalIgnoreCase)).ToArray() is [var icon]
        ? icon : null;

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

    // Deletes the imported donor template.
    public static void Remove(string directory)
    {
        _GATE.Wait();
        try
        {
            TemplateFolder.Delete(directory);
        }
        finally
        {
            _GATE.Release();
        }
    }

    // The data.bin header and game chip of a decrypted SNES VC .cia, .cxi or .app; Settings
    // reads chip presets from it.
    public static SnesDonor ReadSnesDonor(string donorPath)
    {
        var parts = Ncch.ParseTitle(donorPath);
        return SnesRom.ReadDonor(SnesData(parts.RomFs.Length > 0 ? RomFs.Read(parts.RomFs) : []));
    }

    // Extracts the template from a decrypted GBC, NES or SNES VC .cia, .cxi or .app.
    public static void ImportDonor(GameSystem system, string donorPath, string directory)
    {
        var parts = Ncch.ParseTitle(donorPath);
        var files = parts.RomFs.Length > 0 ? RomFs.Read(parts.RomFs) : [];
        if (!parts.ExefsFiles.TryGetValue(".code", out var code))
            throw new InvalidDataException($"Not {Article(system)} Virtual Console title (no "
                + "emulator code).");
        string romName;
        byte[]? snesHeader = null;
        if (system == GameSystem.Snes)
        {
            snesHeader = SnesRom.ReadDonor(SnesData(files)).Header;
            romName = _SNES_DATA;
        }
        else
        {
            var games = files.Keys
                .Where(name => name.StartsWith(_ROM_FOLDER, StringComparison.Ordinal)).ToList();
            if (!files.ContainsKey(_CONFIG) || games.Count != 1)
                throw new InvalidDataException($"Not {Article(system)} Virtual Console title (no "
                    + "/rom/ and config.ini in its RomFS).");
            CheckDonorGame(system, files[games[0]]);
            romName = games[0];
        }
        var kept = files.Where(file => file.Key != romName
            && !file.Key.StartsWith(_ROM_FOLDER, StringComparison.Ordinal)
            && !file.Key.EndsWith(".patch", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(StringComparer.Ordinal);
        var logo = parts.ExefsFiles.GetValueOrDefault("logo") is { Length: > 0 } exefsLogo
            ? exefsLogo : parts.LogoRegion;
        var template = new VcTemplate(parts.Exheader, code, logo, kept, romName, snesHeader);
        if (template.Problem(system) is { } problem)
            throw new InvalidDataException($"Template rejected: {problem}.");
        IEnumerable<byte[]> contents = [template.Exheader, code, logo, RomFs.Build(kept),
            Encoding.UTF8.GetBytes(romName)];
        if (snesHeader is not null) contents = contents.Append(snesHeader);
        _GATE.Wait();
        try
        {
            TemplateFolder.Replace(directory, FileNames(system).Zip(contents));
        }
        finally
        {
            _GATE.Release();
        }
    }

    private static byte[] SnesData(Dictionary<string, byte[]> files) =>
        files.TryGetValue(_SNES_DATA, out var data) ? data
        : throw new InvalidDataException("Not an SNES Virtual Console title (no data.bin in its "
            + "RomFS).");

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

    private static string[] FileNames(GameSystem system) =>
        system == GameSystem.Snes ? [.. _FILES, .. _SNES_FILES] : _FILES;

    private static VcTemplate Read(GameSystem system, string directory)
    {
        var names = FileNames(system);
        if (!names.All(name => File.Exists(Path.Combine(directory, name))))
            throw new VcTemplateMissingException(
                $"{Label(system)} template is not installed in {directory}. {DonorHint(system)}");
        var parts = names.Select(name => File.ReadAllBytes(Path.Combine(directory, name))).ToArray();
        VcTemplate template;
        try
        {
            template = new VcTemplate(parts[0], parts[1], parts[2], RomFs.Read(parts[3]),
                Encoding.UTF8.GetString(parts[4]),
                system == GameSystem.Snes ? parts[5] : null);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException)
        {
            throw new VcTemplateMissingException($"{Label(system)} template in {directory} is "
                + $"damaged ({error.Message}). {DonorHint(system)}");
        }
        if (template.Problem(system) is { } problem)
            throw new VcTemplateMissingException($"{Label(system)} template in {directory} is "
                + $"damaged ({problem}). {DonorHint(system)}");
        return template;
    }

    private static string Label(GameSystem system) => system switch
    {
        GameSystem.Gb => "GB/GBC",
        GameSystem.Nes => "NES",
        GameSystem.Snes => "SNES",
        _ => throw new ArgumentOutOfRangeException(nameof(system), system, null),
    };

    private static string Article(GameSystem system) => system switch
    {
        GameSystem.Gb => "a Game Boy",
        GameSystem.Nes => "an NES",
        GameSystem.Snes => "an SNES",
        _ => throw new ArgumentOutOfRangeException(nameof(system), system, null),
    };

    private static string DonorHint(GameSystem system) => system switch
    {
        GameSystem.Gb => "Import a decrypted Game Boy Color Virtual Console title in Settings > "
            + "Import GB/GBC Donor CIA",
        GameSystem.Nes => "Import a decrypted NES Virtual Console title in Settings > Import NES "
            + "Donor CIA",
        GameSystem.Snes => "Import a decrypted New 3DS SNES Virtual Console title in Settings > "
            + "Import SNES Donor CIA",
        _ => throw new ArgumentOutOfRangeException(nameof(system), system, null),
    } + " (GodMode9: Title manager -> the game -> Build CIA (standard)).";

    private string? Problem(GameSystem system) =>
        Exheader.Length != Ncch.EXHEADER_FULL_SIZE ? "exheader has the wrong size"
        : Code.Length == 0 ? "emulator code is empty"
        : Logo.Length == 0 ? "boot logo is empty"
        : system == GameSystem.Snes ? SnesProblem()
        : !Files.ContainsKey(_CONFIG) ? "RomFS has no config.ini"
        : !RomName.StartsWith(_ROM_FOLDER, StringComparison.Ordinal)
            || RomName.Length == _ROM_FOLDER.Length ? "ROM path is not in /rom/"
        : null;

    private string? SnesProblem() =>
        RomName != _SNES_DATA ? "ROM path is not /data.bin"
        : SnesHeader?.Length != SnesRom.DATA_HEADER_SIZE
            ? "data.bin header is missing"
        : SnesIcon is null ? "RomFS has no single .icn file"
        : null;
}

public sealed class VcTemplateMissingException(string message) : Exception(message);
