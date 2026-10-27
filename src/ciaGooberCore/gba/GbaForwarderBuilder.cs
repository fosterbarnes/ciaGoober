namespace CiaGoober.Core;

public static class GbaForwarderBuilder
{
    // The CXI that makerom wraps with -content: the ROM plus AGB_FIRM footer as ExeFS .code,
    // the bannertool banner and SMDH, and the template. Logs the resolved save type.
    public static byte[] BuildCxi(string romPath, GbaOptions options, byte[] banner, byte[] smdh,
        GbaTemplate template, int uniqueId, string gameCode, IProgress<string> log)
    {
        var rom = File.ReadAllBytes(romPath);
        GbaFooter.CheckRomSize(rom.Length);
        if (!GbaFooter.HeaderValid(rom))
            log.Report("Warning: the ROM header checksum is invalid (corrupt ROM or ROM hack).");
        // Auto detects on every build; nothing per game is remembered.
        var saveType = SaveTypes.Parse(options.SaveType) ?? SaveTypes.Detect(rom);
        var code = GbaFooter.BuildCode(rom, saveType, options.Screen, options.Ghosting);
        log.Report($"Save type: {saveType.Label()}; screen: {options.Screen}; "
            + $"ghosting: {options.Ghosting}");

        var titleId = 0x0004000000000000UL | (ulong)uniqueId << 8;
        var exheader = Ncch.PatchExheader(template.Exheader, titleId, gameCode,
            (ulong)saveType.SdSaveSize());
        var exefs = Ncch.BuildExefs((".code", code), ("banner", banner), ("icon", smdh),
            ("logo", template.Logo));
        return Ncch.BuildNcch(titleId, $"CTR-H-{gameCode}", exheader, exefs, template.RomFs);
    }
}
