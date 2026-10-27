using System.Security.Cryptography;

namespace CiaGoober.Core;

// Nintendo's signed GBA exheader, empty RomFS and boot logo, shared by every GBA CIA.
public sealed record GbaTemplate(byte[] Exheader, byte[] RomFs, byte[] Logo)
{
    private static readonly string[] _FILES = ["exheader.bin", "romfs.bin", "logo.bin"];
    // Copy pinned by vcinject (src/vcinject/template.py): remote name and SHA-256 per local file.
    private const string _DOWNLOAD_BASE = "https://raw.githubusercontent.com/vedoot/vcoven/"
        + "9aed5e2b31d5f19eb76098153bfbfa38ca6e3dd5/template/";
    private static readonly (string Remote, string Sha256)[] _DOWNLOADS =
    [
        ("exheader.bin", "44f8f254992ba7355b85d41262336ec10e1252dc0750a1628a609a2143cb32a9"),
        ("romfs.bin", "afcd2d8fd818654d196a385f7207d8011644f89b168dbf6fa9c4904515088f3d"),
        ("logo.darc.lz", "2a98c49d919e254e15dc213cab47a800ed63b248dcd43119e8fb82d9e62ae51c"),
    ];
    private const string _DONOR_HINT = "Import a decrypted GBA Virtual Console CIA in Settings > "
        + "Import GBA Donor CIA: any inject made with NSUI or Ultimate GBA VC Injector, or a 3DS "
        + "Ambassador GBA game (for example Metroid Fusion) rebuilt in GodMode9 with Build CIA "
        + "(standard).";
    private static readonly HttpClient _HTTP = new() { Timeout = TimeSpan.FromSeconds(30) };
    // Parallel GBA builds share one template folder, and Save replaces it.
    private static readonly SemaphoreSlim _GATE = new(1, 1);

    public static bool IsInstalled(string directory)
    {
        try
        {
            Load(directory);
            return true;
        }
        catch (GbaTemplateMissingException)
        {
            return false;
        }
    }

    // Throws GbaTemplateMissingException when missing or damaged.
    public static GbaTemplate Load(string directory)
    {
        if (!_FILES.All(name => File.Exists(Path.Combine(directory, name))))
            throw new GbaTemplateMissingException(
                $"GBA template is not installed in {directory}. {_DONOR_HINT}");
        var parts = _FILES.Select(name => File.ReadAllBytes(Path.Combine(directory, name))).ToArray();
        var template = new GbaTemplate(parts[0], parts[1], parts[2]);
        if (template.Problem() is { } problem)
            throw new GbaTemplateMissingException(
                $"GBA template in {directory} is damaged ({problem}). {_DONOR_HINT}");
        return template;
    }

    // Load, else download the pinned copy (SHA-256 checked) and load.
    public static async Task<GbaTemplate> EnsureAsync(string directory, IProgress<string> log,
        CancellationToken ct)
    {
        await _GATE.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadOrDownloadAsync(directory, log, ct).ConfigureAwait(false);
        }
        finally
        {
            _GATE.Release();
        }
    }

    // Deletes the installed template; the next GBA build downloads the pinned copy. Blocks while
    // a build loads the template, so call it off the UI thread.
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

    // Extracts the template from a decrypted GBA VC .cia, .cxi or .app. Blocks while a build
    // downloads the template, so call it off the UI thread.
    public static void ImportDonor(string donorPath, string directory)
    {
        var parts = Ncch.ParseTitle(donorPath);
        var code = parts.ExefsFiles.GetValueOrDefault(".code", []);
        if (code.Length < 0x10 || !code.AsSpan(code.Length - 0x10, 4).SequenceEqual(".CAA"u8))
            throw new InvalidDataException("Not a GBA Virtual Console title (no AGB_FIRM footer).");
        var logo = parts.ExefsFiles.GetValueOrDefault("logo") is { Length: > 0 } exefsLogo
            ? exefsLogo : parts.LogoRegion;
        _GATE.Wait();
        try
        {
            new GbaTemplate(parts.Exheader, parts.RomFs, logo).Save(directory);
        }
        finally
        {
            _GATE.Release();
        }
    }

    private static async Task<GbaTemplate> LoadOrDownloadAsync(string directory,
        IProgress<string> log, CancellationToken ct)
    {
        try
        {
            return Load(directory);
        }
        catch (GbaTemplateMissingException)
        {
            log.Report("Downloading the GBA Virtual Console template...");
        }
        var parts = new byte[_DOWNLOADS.Length][];
        for (var i = 0; i < parts.Length; i++)
        {
            var (remote, sha256) = _DOWNLOADS[i];
            try
            {
                parts[i] = await _HTTP.GetByteArrayAsync(_DOWNLOAD_BASE + remote, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException
                || error is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new GbaTemplateMissingException(
                    $"GBA template download failed ({error.Message}). {_DONOR_HINT}");
            }
            if (Convert.ToHexStringLower(SHA256.HashData(parts[i])) != sha256)
                throw new GbaTemplateMissingException(
                    $"GBA template download of {_FILES[i]} has the wrong checksum. {_DONOR_HINT}");
        }
        new GbaTemplate(parts[0], parts[1], parts[2]).Save(directory);
        return Load(directory);
    }

    private string? Problem() =>
        Exheader.Length != Ncch.EXHEADER_FULL_SIZE ? "exheader has the wrong size"
        : !RomFs.AsSpan().StartsWith("IVFC"u8) ? "RomFS is not an IVFC image"
        : Logo.Length == 0 ? "boot logo is empty"
        : null;

    private void Save(string directory)
    {
        if (Problem() is { } problem)
            throw new InvalidDataException($"Template rejected: {problem}.");
        TemplateFolder.Replace(directory, _FILES.Zip((byte[][])[Exheader, RomFs, Logo]));
    }
}

public sealed class GbaTemplateMissingException(string message) : Exception(message);
