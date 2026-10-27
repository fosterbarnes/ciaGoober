namespace CiaGoober.Core;

public static class ForwarderBuilder
{
    // SMDH title languages in bannertool's flag prefixes (generator.py:269-280).
    private static readonly (string Language, string Prefix)[] _SMDH_LANGUAGES =
    [
        ("eng", ""), ("jpn", "j"), ("fra", "f"), ("ger", "g"), ("ita", "i"), ("spa", "s"),
        ("chn", "sc"), ("kor", "k"),
    ];

    // Safe to call in parallel: each job works in its own folder under AppPaths.Jobs.
    // Port of generator.py generate()/makecia() (667-730) and the GBA flow (733-838).
    public static async Task<BuildResult> BuildAsync(BuildJob job, ForwarderRegistry registry,
        IProgress<string> log, CancellationToken ct)
    {
        var ds = job.System == GameSystem.Nds;
        var rom = job.System switch
        {
            GameSystem.Nds => NdsRom.Read(job.RomPath),
            GameSystem.Gba => GbaRom.Read(job.RomPath),
            _ => GbRom.Read(job.RomPath),
        };
        var key = ds ? SdPath.Resolve(job.RomPath, job.CustomSdPath, job.SdRomFolder)
            : SdPath.EmbeddedKey(job.RomPath, job.System);
        var output = Path.GetFullPath(OutputNaming.CiaPath(job.OutputFolder,
            registry.Find(job.System, key), job.System, key, job.RomPath));
        var entry = await registry.ReserveAsync(job.System, key,
            Path.GetFileNameWithoutExtension(output)).ConfigureAwait(false);
        // DS CIAs carry the registered spelling of the SD path (generator.py:623-625).
        var sdPath = ds ? entry.Rom : null;
        if (ds) log.Report($"Using ROM path: {sdPath}");
        log.Report($"Output file: {output}");
        if (File.Exists(output) && !CiaPublisher.Owns(output, entry.UniqueId, sdPath))
            throw new IOException($"Refusing to overwrite unrelated CIA: {output}");

        var folder = Directory.CreateDirectory(
            Path.Combine(AppPaths.Jobs, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            log.Report("Extracting and resizing icon...");
            File.WriteAllBytes(Path.Combine(folder, "icon.png"), RenderIcon(job));
            log.Report("Creating SMDH...");
            await ToolRunner.RunAsync("bannertool", folder, ["makesmdh", "-i", "icon.png",
                .. SmdhTitleArguments(job, rom), "-o", "output.smdh"], ct).ConfigureAwait(false);
            var sound = await PrepareBannerAndSoundAsync(job, rom, folder, log, ct)
                .ConfigureAwait(false);
            log.Report("Creating banner...");
            await ToolRunner.RunAsync("bannertool", folder,
                ["makebanner", "-i", "banner.png", "-a", sound, "-o", "banner.bin"], ct)
                .ConfigureAwait(false);
            log.Report("Running makerom...");
            await (job.System switch
            {
                GameSystem.Nds => BuildDsCiaAsync(job, rom, entry, folder, ct),
                GameSystem.Gba => BuildGbaCiaAsync(job, rom, entry, folder, log, ct),
                _ => BuildGbCiaAsync(job, rom, entry, folder, log, ct),
            }).ConfigureAwait(false);
            var built = Path.Combine(folder, "build.cia");
            if (CiaReader.UniqueId(built) != entry.UniqueId)
                throw new InvalidDataException("makerom did not produce the requested CIA ID.");
            ct.ThrowIfCancellationRequested();
            CiaPublisher.Publish(built, output, entry.UniqueId, sdPath);
            log.Report("CIA generated.");
            return new BuildResult(output, entry.UniqueId);
        }
        finally
        {
            DeleteJobFolder(folder);
        }
    }

    // Removes job folders left by a crash; the app calls this at startup before any build. A
    // folder that is still locked waits for the next start.
    public static void DeleteStaleJobs()
    {
        if (!Directory.Exists(AppPaths.Jobs)) return;
        foreach (var folder in Directory.EnumerateDirectories(AppPaths.Jobs))
            DeleteJobFolder(folder);
    }

    // Card icon image, else the DS ROM icon, else (GBA, GB) the banner image
    // (generator.py:195-212, 790-795).
    private static byte[] RenderIcon(BuildJob job)
    {
        if (job.System == GameSystem.Nds && (job.UseRomIcon || job.Icon.Source is null))
            return ArtImages.RenderRomIcon(NdsRom.Icon(job.RomPath), job.IconBackground);
        var art = job.Icon.Source is null ? job.Banner : job.Icon;
        if (art.Source is null)
            throw new InvalidOperationException(
                "GBA and Game Boy ROMs have no icon: choose a banner or icon image.");
        return ArtImages.RenderIcon(art.Source, art.Crop, job.IconBackground);
    }

    // Unedited DS titles use every banner language like generator.py:269-280; edited titles, GBA
    // and GB use the card's three lines for all languages.
    private static IEnumerable<string> SmdhTitleArguments(BuildJob job, RomInfo rom)
    {
        var unedited = job.System == GameSystem.Nds && job.TitleLines.SequenceEqual(rom.TitleLines);
        var languages = unedited ? NdsRom.LanguageTitles(job.RomPath) : null;
        if (languages is null || languages.Values.All(lines => lines is null))
        {
            string[] lines = [.. job.TitleLines, "", "", ""];
            return _SMDH_LANGUAGES.SelectMany(language => (string[])[
                $"-{language.Prefix}s", lines[0], $"-{language.Prefix}l", lines[1],
                $"-{language.Prefix}p", lines[2]]);
        }
        return _SMDH_LANGUAGES.SelectMany(language =>
        {
            if (languages.GetValueOrDefault(language.Language) is not { } lines) return [];
            var text = string.Join(' ', lines[..^1]);
            return (string[])[$"-{language.Prefix}s", text, $"-{language.Prefix}l", text,
                $"-{language.Prefix}p", lines[^1]];
        });
    }

    // Bundled per-system banner sound: <system>boot.ogg, else <system>boot.wav.
    public static string DefaultSoundPath(GameSystem system)
    {
        var name = system switch
        {
            GameSystem.Nds => "dsboot",
            GameSystem.Gba => "gbaboot",
            _ => "gbcboot",
        };
        var ogg = Path.Combine(AppPaths.Tools, name + ".ogg");
        return File.Exists(ogg) ? ogg : Path.Combine(AppPaths.Tools, name + ".wav");
    }

    // Writes banner.png and the sound file; returns the sound's job-folder name.
    private static async Task<string> PrepareBannerAndSoundAsync(BuildJob job, RomInfo rom,
        string folder, IProgress<string> log, CancellationToken ct)
    {
        var ds = job.System == GameSystem.Nds;
        // A GBA or GB card without a banner uses its icon choice (generator.py:830).
        var banner = ds || job.Banner.Source is not null ? job.Banner : job.Icon;
        var bannerSource = banner.Source;
        if (bannerSource is null)
        {
            log.Report("No banner provided. Checking online assets and GameTDB...");
            bannerSource = await DsForwarderBuilder.DownloadBannerAsync(rom.GameCode, folder, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Banner was not found.");
        }
        var soundSource = job.SoundPath
            ?? (ds ? await DsForwarderBuilder.DownloadSoundAsync(rom.GameCode, folder, ct)
                .ConfigureAwait(false) : null)
            ?? DefaultSoundPath(job.System);
        log.Report($"Using sound file: {soundSource}");
        log.Report($"Using banner image: {bannerSource}");
        File.WriteAllBytes(Path.Combine(folder, "banner.png"), ArtImages.RenderBanner(bannerSource,
            banner.Source is null ? null : banner.Crop));
        var sound = "sound" + Path.GetExtension(soundSource).ToLowerInvariant();
        // A downloaded sound is already saved as sound.wav.
        if (Path.GetDirectoryName(soundSource) != folder)
            File.Copy(soundSource, Path.Combine(folder, sound));
        return sound;
    }

    private static async Task BuildDsCiaAsync(BuildJob job, RomInfo rom, RegistryEntry entry,
        string folder, CancellationToken ct)
    {
        foreach (var file in (string[])["forwarder.elf", "build-cia.rsf"])
            File.Copy(Path.Combine(AppPaths.Tools, file), Path.Combine(folder, file));
        DsForwarderBuilder.WriteRomFs(folder, entry.Rom);
        await ToolRunner.RunAsync("makerom", folder,
            DsForwarderBuilder.MakeromArguments(job.TitleLines[0], rom.GameCode, entry.UniqueId),
            ct).ConfigureAwait(false);
    }

    // generator.py:797-809.
    private static async Task BuildGbaCiaAsync(BuildJob job, RomInfo rom, RegistryEntry entry,
        string folder, IProgress<string> log, CancellationToken ct)
    {
        var template = await GbaTemplate.EnsureAsync(AppPaths.GbaTemplate, log, ct)
            .ConfigureAwait(false);
        var cxi = GbaForwarderBuilder.BuildCxi(job.RomPath,
            job.Gba ?? throw new ArgumentException("GBA build needs GBA options.", nameof(job)),
            File.ReadAllBytes(Path.Combine(folder, "banner.bin")),
            File.ReadAllBytes(Path.Combine(folder, "output.smdh")), template, entry.UniqueId,
            rom.GameCode, log);
        await WrapCxiAsync(cxi, folder, ct).ConfigureAwait(false);
    }

    private static async Task BuildGbCiaAsync(BuildJob job, RomInfo rom, RegistryEntry entry,
        string folder, IProgress<string> log, CancellationToken ct)
    {
        var template = await Task.Run(() => GbTemplate.Load(AppPaths.GbTemplate), ct)
            .ConfigureAwait(false);
        var cxi = GbForwarderBuilder.BuildCxi(job.RomPath,
            File.ReadAllBytes(Path.Combine(folder, "banner.bin")),
            File.ReadAllBytes(Path.Combine(folder, "output.smdh")), template, entry.UniqueId,
            rom.GameCode, log);
        await WrapCxiAsync(cxi, folder, ct).ConfigureAwait(false);
    }

    private static async Task WrapCxiAsync(byte[] cxi, string folder, CancellationToken ct)
    {
        File.WriteAllBytes(Path.Combine(folder, "forwarder.cxi"), cxi);
        await ToolRunner.RunAsync("makerom", folder, ["-f", "cia", "-ignoresign", "-target", "t",
            "-content", "forwarder.cxi:0:0", "-o", "build.cia"], ct).ConfigureAwait(false);
    }

    // A tool killed on cancellation can still hold a file briefly; DeleteStaleJobs removes what
    // is left at the next start, so a failed cleanup must not hide the build's own result.
    private static void DeleteJobFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
