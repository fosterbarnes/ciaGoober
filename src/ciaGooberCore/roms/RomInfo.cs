namespace CiaGoober.Core;

// Gb covers Game Boy and Game Boy Color.
public enum GameSystem { Nds, Gba, Gb, Nes }

// GameCode is the 4-character code used in the CIA product code (CTR-H-<code>).
// TitleLines are the default HOME Menu title lines: [short, long, publisher] (publisher may be "").
public sealed record RomInfo(string Path, GameSystem System, string GameCode, string[] TitleLines,
    string InternalTitle);
