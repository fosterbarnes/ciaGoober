namespace CiaGoober.Core;

// Gb covers Game Boy and Game Boy Color.
public enum GameSystem { Nds, Gba, Gb, Nes }

// GameCode is the 4-character code used in the CIA product code (CTR-H-<code>).
// TitleLines are the default HOME Menu title lines: [short, long, publisher] (publisher may be "").
// Identity is set for DS ROMs only (SD card matching).
public sealed record RomInfo(string Path, GameSystem System, string GameCode, string[] TitleLines,
    string InternalTitle)
{
    public NdsIdentity? Identity { get; init; }
}

// A DS ROM's header game code plus header CRC16 (0x15E). Equal for trimmed and untrimmed copies
// and for renamed files; ROM hacks often keep the original header, so equal is not unique.
public sealed record NdsIdentity(string GameCode, ushort HeaderCrc);
