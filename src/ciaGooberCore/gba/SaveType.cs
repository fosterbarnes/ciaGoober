using System.Text;

namespace CiaGoober.Core;

// AGB_FIRM save chip IDs (footer 0x08). Member names are the stored option values (vcinject names).
public enum SaveType
{
    EEPROM_8K = 0x0,
    EEPROM_8K_LARGE_ROM = 0x1,
    EEPROM_64K = 0x2,
    EEPROM_64K_LARGE_ROM = 0x3,
    FLASH_512K_ATMEL_RTC = 0x4,
    FLASH_512K_ATMEL = 0x5,
    FLASH_512K_SST_RTC = 0x6,
    FLASH_512K_SST = 0x7,
    FLASH_512K_PANASONIC_RTC = 0x8,
    FLASH_512K_PANASONIC = 0x9,
    FLASH_1M_MACRONIX_RTC = 0xA,
    FLASH_1M_MACRONIX = 0xB,
    FLASH_1M_SANYO_RTC = 0xC,
    FLASH_1M_SANYO = 0xD,
    SRAM_256K = 0xE,
    NONE = 0xF,
}

public static class SaveTypes
{
    private const int _AGBSAVE_PARTITION_SIZE = 0x30000;

    private static readonly string[] _LABELS =
    [
        "EEPROM 4 Kbit", "EEPROM 4 Kbit (ROM > 16 MiB)", "EEPROM 64 Kbit",
        "EEPROM 64 Kbit (ROM > 16 MiB)", "Flash 512 Kbit Atmel + RTC", "Flash 512 Kbit Atmel",
        "Flash 512 Kbit SST + RTC", "Flash 512 Kbit SST", "Flash 512 Kbit Panasonic + RTC",
        "Flash 512 Kbit Panasonic", "Flash 1 Mbit Macronix + RTC", "Flash 1 Mbit Macronix",
        "Flash 1 Mbit Sanyo + RTC", "Flash 1 Mbit Sanyo", "SRAM/FRAM 256 Kbit", "No save",
    ];

    // Emulated save chip sizes, matching libn3ds' sizeShiftLut.
    private static readonly int[] _SAVE_BYTES =
    [
        0x200, 0x200, 0x2000, 0x2000, 0x10000, 0x10000, 0x10000, 0x10000, 0x10000, 0x10000,
        0x20000, 0x20000, 0x20000, 0x20000, 0x8000, 0,
    ];

    // Short names; "auto" maps to null (detect from the ROM).
    private static readonly Dictionary<string, SaveType?> _ALIASES = new()
    {
        ["auto"] = null, ["none"] = SaveType.NONE, ["sram"] = SaveType.SRAM_256K,
        ["eeprom4k"] = SaveType.EEPROM_8K, ["eeprom64k"] = SaveType.EEPROM_64K,
        ["flash512"] = SaveType.FLASH_512K_PANASONIC,
        ["flash512-rtc"] = SaveType.FLASH_512K_PANASONIC_RTC,
        ["flash1m"] = SaveType.FLASH_1M_MACRONIX, ["flash1m-rtc"] = SaveType.FLASH_1M_MACRONIX_RTC,
    };

    // Homebrew overrides keyed by the first game code character (open_agb_firm conventions).
    private static readonly Dictionary<byte, SaveType> _HOMEBREW_OVERRIDES = new()
    {
        [(byte)'1'] = SaveType.EEPROM_64K, [(byte)'2'] = SaveType.SRAM_256K,
        [(byte)'3'] = SaveType.FLASH_512K_PANASONIC_RTC,
        [(byte)'4'] = SaveType.FLASH_1M_MACRONIX_RTC,
        [(byte)'F'] = SaveType.EEPROM_8K, [(byte)'S'] = SaveType.SRAM_256K,
    };

    // SDK library ID strings such as "FLASH1M_V103"; no kind contains 'V' or is a suffix of
    // another, so each "_V" has at most one kind and the first valid "_V" is the leftmost match.
    private static readonly byte[][] _SDK_KINDS =
        [.. new[] { "EEPROM", "SRAM_F", "SRAM", "FLASH1M", "FLASH512", "FLASH" }
            .Select(kind => Encoding.ASCII.GetBytes(kind))];

    public static string Label(this SaveType type) => _LABELS[(int)type];
    public static bool HasRtc(this SaveType type) => type.ToString().EndsWith("_RTC");
    public static int SaveBytes(this SaveType type) => _SAVE_BYTES[(int)type];

    // ExtHeader/TMD SaveDataSize: NATIVE_FIRM keeps two slots of AGBSAVE header (0x200) + save and
    // drops the save if the file has any other size.
    public static int SdSaveSize(this SaveType type) =>
        Math.Max(_AGBSAVE_PARTITION_SIZE, 2 * (0x200 + type.SaveBytes()));

    // Alias or SaveType name, case-insensitive; null for "auto".
    public static SaveType? Parse(string text)
    {
        var key = text.Trim();
        if (_ALIASES.TryGetValue(key.ToLowerInvariant(), out var alias)) return alias;
        var name = key.ToUpperInvariant();
        foreach (var type in Enum.GetValues<SaveType>())
            if (type.ToString() == name) return type;
        throw new ArgumentException($"Unknown GBA save type: {text}");
    }

    public static SaveType Detect(ReadOnlySpan<byte> rom)
    {
        var gameCode = rom.Slice(0xAC, 4);
        var end = gameCode.IndexOf((byte)0);
        if (end >= 0) gameCode = gameCode[..end];
        var first = gameCode.IndexOfAnyExcept(" \t\n\v\f\r\x1c\x1d\x1e\x1f"u8);
        if (first >= 0 && _HOMEBREW_OVERRIDES.TryGetValue(gameCode[first], out var homebrew))
            return AdjustForRomSize(homebrew, rom.Length);

        var kind = FindSdkKind(rom, out var version);
        if (kind is null) return SaveType.NONE;
        var hasRtc = rom.IndexOf("SIIRTC_V"u8) >= 0;
        var save = kind switch
        {
            "EEPROM" => version is "121" or "124" ? SaveType.EEPROM_64K : SaveType.EEPROM_8K,
            "SRAM" or "SRAM_F" => SaveType.SRAM_256K,
            "FLASH1M" => hasRtc ? SaveType.FLASH_1M_MACRONIX_RTC : SaveType.FLASH_1M_MACRONIX,
            _ => hasRtc ? SaveType.FLASH_512K_PANASONIC_RTC : SaveType.FLASH_512K_PANASONIC,
        };
        return AdjustForRomSize(save, rom.Length);
    }

    private static string? FindSdkKind(ReadOnlySpan<byte> rom, out string version)
    {
        for (int offset = 0, hit; (hit = rom[offset..].IndexOf("_V"u8)) >= 0; offset += hit + 1)
        {
            var at = offset + hit;
            if (at + 5 > rom.Length || !char.IsAsciiDigit((char)rom[at + 2])
                || !char.IsAsciiDigit((char)rom[at + 3]) || !char.IsAsciiDigit((char)rom[at + 4]))
                continue;
            foreach (var kind in _SDK_KINDS)
            {
                if (at < kind.Length || !rom.Slice(at - kind.Length, kind.Length).SequenceEqual(kind))
                    continue;
                version = Encoding.ASCII.GetString(rom.Slice(at + 2, 3));
                return Encoding.ASCII.GetString(kind);
            }
        }
        version = "";
        return null;
    }

    // EEPROM is addressed differently on carts with more than 16 MiB of ROM.
    private static SaveType AdjustForRomSize(SaveType save, int romLength) =>
        romLength > 0x1000000 && save is SaveType.EEPROM_8K or SaveType.EEPROM_64K ? save + 1 : save;
}
