namespace CiaGoober.Core;

// iNES and NES 2.0 ROMs (NESdev "iNES", "NES 2.0") and the TNES image the 3DS NES Virtual
// Console reads (NESdev "TNES"): a 16-byte header, then PRG ROM, then CHR ROM.
public static class NesRom
{
    private const int _HEADER_SIZE = 16;
    private const int _BANK = 0x2000;
    private const int _MAX_BANKS = 255;
    private const int _MAX_FILE_SIZE = 8 * 1024 * 1024;
    private const string _SYSTEM_NAME = "Nintendo Entertainment System";

    // iNES mapper -> TNES mapper; the emulator knows no others.
    private static readonly Dictionary<int, byte> _TNES_MAPPERS = new()
    {
        [0] = 0, [1] = 1, [9] = 2, [4] = 3, [10] = 4, [5] = 5, [2] = 6, [3] = 7, [7] = 9, [86] = 31,
    };
    // Boards whose chip switches mirroring (TNES mirroring 0); the rest have it wired.
    private static readonly HashSet<int> _MAPPER_MIRRORING = [1, 4, 5, 7, 9, 10];
    // Boards that commonly carry 8 KB PRG RAM even without a battery.
    private static readonly HashSet<int> _WORK_RAM = [1, 4, 5];

    // PrgBanks and ChrBanks in 8 KB units.
    private readonly record struct Header(int Mapper, int PrgBanks, int ChrBanks, bool Battery,
        bool Vertical, bool FourScreen, bool Pal);

    // Throws InvalidDataException for a ROM the NES VC emulator cannot run.
    public static RomInfo Read(string path)
    {
        var header = new byte[_HEADER_SIZE];
        long length;
        using (var stream = File.OpenRead(path))
        {
            if (stream.ReadAtLeast(header, _HEADER_SIZE, throwOnEndOfStream: false) < _HEADER_SIZE)
                throw new InvalidDataException($"File is too small to be an NES ROM: {path}");
            length = stream.Length;
        }
        Parse(header, length);
        var name = RomNames.FromFileName(path, "", _SYSTEM_NAME);
        return new RomInfo(path, GameSystem.Nes, "NESH", [name, name, _SYSTEM_NAME], "");
    }

    // The TNES image and the cartridge RAM it declares in bytes; data after CHR ROM (PlayChoice
    // and NES 2.0 misc ROMs) is dropped.
    public static (byte[] Tnes, int RamSize) ToTnes(byte[] ines, IProgress<string> log)
    {
        var header = Parse(ines, ines.Length);
        if (header.FourScreen)
            log.Report("Warning: TNES has no four-screen mirroring; graphics may glitch.");
        if (header.Pal)
            log.Report("Warning: PAL ROMs may run too fast on the NES Virtual Console emulator.");
        var workRam = header.Battery || _WORK_RAM.Contains(header.Mapper);
        var dataSize = (header.PrgBanks + header.ChrBanks) * _BANK;
        var tnes = new byte[_HEADER_SIZE + dataSize];
        "TNES"u8.CopyTo(tnes);
        tnes[4] = _TNES_MAPPERS[header.Mapper];
        tnes[5] = (byte)header.PrgBanks;
        tnes[6] = (byte)header.ChrBanks;
        tnes[7] = (byte)(workRam ? 1 : 0);
        tnes[8] = (byte)(_MAPPER_MIRRORING.Contains(header.Mapper) ? 0 : header.Vertical ? 2 : 1);
        tnes[9] = (byte)(header.Battery ? 1 : 0);
        ines.AsSpan(_HEADER_SIZE, dataSize).CopyTo(tnes.AsSpan(_HEADER_SIZE));
        return (tnes, workRam ? _BANK : 0);
    }

    public static bool IsTnes(ReadOnlySpan<byte> rom) =>
        rom.Length >= _HEADER_SIZE && rom.StartsWith("TNES"u8);

    private static Header Parse(ReadOnlySpan<byte> h, long length)
    {
        if (!h.StartsWith("NES\u001A"u8)) throw new InvalidDataException("Not an iNES ROM (.nes).");
        if (length > _MAX_FILE_SIZE)
            throw new InvalidDataException("NES ROMs can be at most 8 MiB.");
        int mapper, prg16, chr8;
        bool pal;
        if ((h[7] & 0x0C) == 0x08)
        {
            // NES 2.0; a 0xF size nibble means exponent notation, only used by huge ROMs.
            if ((h[9] & 0x0F) == 0x0F || (h[9] & 0xF0) == 0xF0)
                throw new InvalidDataException("NES 2.0 exponent ROM sizes are not supported.");
            mapper = (h[6] >> 4) | (h[7] & 0xF0) | ((h[8] & 0x0F) << 8);
            prg16 = h[4] | ((h[9] & 0x0F) << 8);
            chr8 = h[5] | ((h[9] & 0xF0) << 4);
            pal = (h[12] & 0x03) == 1;
        }
        else
        {
            // Old headers with text in bytes 12-15 ("DiskDude!") carry no upper mapper nibble.
            var junk = h[12.._HEADER_SIZE].ContainsAnyExcept((byte)0);
            mapper = (h[6] >> 4) | (junk ? 0 : h[7] & 0xF0);
            prg16 = h[4];
            chr8 = h[5];
            pal = (h[9] & 0x01) != 0;
        }
        if ((h[6] & 0x04) != 0)
            throw new InvalidDataException("ROMs with a trainer are not supported.");
        if (!_TNES_MAPPERS.ContainsKey(mapper))
            throw new InvalidDataException(
                $"Mapper {mapper} is not supported by the NES Virtual Console emulator.");
        if (prg16 == 0 || prg16 * 2 > _MAX_BANKS || chr8 > _MAX_BANKS)
            throw new InvalidDataException("Unsupported PRG or CHR ROM size.");
        if (length < _HEADER_SIZE + (prg16 * 2L + chr8) * _BANK)
            throw new InvalidDataException("The ROM is shorter than its header says.");
        return new Header(mapper, prg16 * 2, chr8, (h[6] & 0x02) != 0, (h[6] & 0x01) != 0,
            (h[6] & 0x08) != 0, pal);
    }
}
