using System.Text;

namespace CiaGoober.Core;

// Nintendo's GB/GBC Virtual Console emulator, taken from an official GBC VC title the user owns:
// the signed exheader, the emulator (.code), the boot logo and the RomFS without the game and its
// patches. RomName is the game's RomFS path, since the emulator loads the one file in /rom/.
// Nothing of it is bundled or downloaded.
public sealed record GbTemplate(byte[] Exheader, byte[] Code, byte[] Logo,
    IReadOnlyDictionary<string, byte[]> Files, string RomName)
{
    private static readonly string[] _FILES =
        ["exheader.bin", "code.bin", "logo.bin", "romfs.bin", "romName.txt"];
    private const string _CONFIG = "/config.ini";
    private const string _ROM_FOLDER = "/rom/";
    private const string _DONOR_HINT = "Import a decrypted Game Boy Color Virtual Console title "
        + "in Settings > Import GB/GBC Donor CIA (GodMode9: Title manager -> the game -> Build "
        + "CIA (standard)).";
    // Parallel GB builds read the folder that an import replaces.
    private static readonly SemaphoreSlim _GATE = new(1, 1);

    public static bool IsInstalled(string directory)
    {
        try
        {
            Load(directory);
            return true;
        }
        catch (GbTemplateMissingException)
        {
            return false;
        }
    }

    // Throws GbTemplateMissingException when missing or damaged.
    public static GbTemplate Load(string directory)
    {
        _GATE.Wait();
        try
        {
            return Read(directory);
        }
        finally
        {
            _GATE.Release();
        }
    }

    // Extracts the template from a decrypted GBC VC .cia, .cxi or .app.
    public static void ImportDonor(string donorPath, string directory)
    {
        var parts = Ncch.ParseTitle(donorPath);
        var files = parts.RomFs.Length > 0 ? RomFs.Read(parts.RomFs) : [];
        var games = files.Keys.Where(name => name.StartsWith(_ROM_FOLDER, StringComparison.Ordinal))
            .ToList();
        if (!files.ContainsKey(_CONFIG) || games.Count != 1
            || !parts.ExefsFiles.TryGetValue(".code", out var code))
            throw new InvalidDataException("Not a Game Boy Virtual Console title (no /rom/ and "
                + "config.ini in its RomFS).");
        var game = files[games[0]];
        if (game.Length < GbRom.MIN_ROM_SIZE || !GbRom.HeaderValid(game))
            throw new InvalidDataException("The donor's game is not a Game Boy ROM.");
        if (!GbRom.IsColor(game))
            throw new InvalidDataException("This is a Game Boy title; use a Game Boy Color "
                + "Virtual Console title, whose emulator also runs Game Boy games.");
        var kept = files.Where(file => !file.Key.StartsWith(_ROM_FOLDER, StringComparison.Ordinal)
            && !file.Key.EndsWith(".patch", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(StringComparer.Ordinal);
        var logo = parts.ExefsFiles.GetValueOrDefault("logo") is { Length: > 0 } exefsLogo
            ? exefsLogo : parts.LogoRegion;
        var template = new GbTemplate(parts.Exheader, code, logo, kept, games[0]);
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

    private static GbTemplate Read(string directory)
    {
        if (!_FILES.All(name => File.Exists(Path.Combine(directory, name))))
            throw new GbTemplateMissingException(
                $"GB/GBC template is not installed in {directory}. {_DONOR_HINT}");
        var parts = _FILES.Select(name => File.ReadAllBytes(Path.Combine(directory, name))).ToArray();
        GbTemplate template;
        try
        {
            template = new GbTemplate(parts[0], parts[1], parts[2], RomFs.Read(parts[3]),
                Encoding.UTF8.GetString(parts[4]));
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException)
        {
            throw new GbTemplateMissingException(
                $"GB/GBC template in {directory} is damaged ({error.Message}). {_DONOR_HINT}");
        }
        if (template.Problem() is { } problem)
            throw new GbTemplateMissingException(
                $"GB/GBC template in {directory} is damaged ({problem}). {_DONOR_HINT}");
        return template;
    }

    private string? Problem() =>
        Exheader.Length != Ncch.EXHEADER_FULL_SIZE ? "exheader has the wrong size"
        : Code.Length == 0 ? "emulator code is empty"
        : Logo.Length == 0 ? "boot logo is empty"
        : !Files.ContainsKey(_CONFIG) ? "RomFS has no config.ini"
        : !RomName.StartsWith(_ROM_FOLDER, StringComparison.Ordinal)
            || RomName.Length == _ROM_FOLDER.Length ? "ROM path is not in /rom/"
        : null;
}

public sealed class GbTemplateMissingException(string message) : Exception(message);
