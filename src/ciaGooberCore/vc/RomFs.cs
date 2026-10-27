using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CiaGoober.Core;

// IVFC RomFS images (3dbrew RomFS): a level 3 file system under a SHA-256 hash tree of 0x1000
// blocks. Files are keyed by "/dir/name". Based on vcinject src/vcinject/romfs.py, with makerom's
// entry order so a built image matches makerom's for the same files.
internal static class RomFs
{
    private const int _BLOCK_LOG = 12;
    private const int _BLOCK = 1 << _BLOCK_LOG;
    private const int _HEADER_SIZE = 0x60;
    private const int _IVFC_HEADER_SIZE = 0x5C;
    private const int _LEVEL3_HEADER_SIZE = 0x28;
    private const int _DIR_ENTRY_SIZE = 0x18;
    private const int _FILE_ENTRY_SIZE = 0x20;
    private const uint _UNUSED = 0xFFFFFFFF;

    public static Dictionary<string, byte[]> Read(byte[] image)
    {
        if (!image.AsSpan().StartsWith("IVFC"u8))
            throw new InvalidDataException("Not a RomFS image.");
        var masterSize = ReadUInt32(image, 0x08);
        var block = 1L << (int)ReadUInt32(image, 0x4C);
        var level3 = image.AsSpan((int)Align(_HEADER_SIZE + masterSize, block));
        var dirMeta = (int)ReadUInt32(level3, 0x0C);
        var fileMeta = (int)ReadUInt32(level3, 0x1C);
        var data = (long)ReadUInt32(level3, 0x24);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // Directories to visit as (entry offset, path); the root is entry 0 with path "/".
        var pending = new Stack<(uint Offset, string Path)>([(0, "/")]);
        while (pending.TryPop(out var dir))
        {
            var entry = level3[(dirMeta + (int)dir.Offset)..];
            for (var file = ReadUInt32(entry, 0x0C); file != _UNUSED;)
            {
                var fileEntry = level3[(fileMeta + (int)file)..];
                var offset = data + BinaryPrimitives.ReadInt64LittleEndian(fileEntry[0x08..]);
                var size = BinaryPrimitives.ReadInt64LittleEndian(fileEntry[0x10..]);
                files[dir.Path + Name(fileEntry, _FILE_ENTRY_SIZE, 0x1C)] =
                    level3.Slice((int)offset, (int)size).ToArray();
                file = ReadUInt32(fileEntry, 0x04);
            }
            for (var child = ReadUInt32(entry, 0x08); child != _UNUSED;)
            {
                var childEntry = level3[(dirMeta + (int)child)..];
                pending.Push((child, dir.Path + Name(childEntry, _DIR_ENTRY_SIZE, 0x14) + "/"));
                child = ReadUInt32(childEntry, 0x04);
            }
        }
        return files;
    }

    // Level 3 first, then levels 1 and 2, each block aligned; the master hash follows the header.
    public static byte[] Build(IReadOnlyDictionary<string, byte[]> files)
    {
        var level3 = BuildLevel3(files);
        var level2 = HashLevel(level3);
        var level1 = HashLevel(level2);
        var master = HashLevel(level1);
        var level2Logical = Align(level1.Length, _BLOCK);
        var level3Logical = Align(level2Logical + level2.Length, _BLOCK);

        using var image = new MemoryStream();
        var header = new byte[_HEADER_SIZE];
        "IVFC"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x04), 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x08), (uint)master.Length);
        var levels = new (long Logical, int Size)[]
            { (0, level1.Length), (level2Logical, level2.Length), (level3Logical, level3.Length) };
        for (var i = 0; i < levels.Length; i++)
        {
            var field = header.AsSpan(0x0C + i * 0x18);
            BinaryPrimitives.WriteInt64LittleEndian(field, levels[i].Logical);
            BinaryPrimitives.WriteInt64LittleEndian(field[0x08..], levels[i].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(field[0x10..], _BLOCK_LOG);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x54), _IVFC_HEADER_SIZE);
        image.Write(header);
        image.Write(master);
        foreach (var level in (byte[][])[level3, level1, level2])
        {
            PadTo(image, _BLOCK);
            image.Write(level);
        }
        PadTo(image, _BLOCK);
        return image.ToArray();
    }

    // Case-insensitive name order like makerom; names that differ only in case stay apart.
    private static readonly Comparer<string> _NAME_ORDER = Comparer<string>.Create((a, b) =>
        string.Compare(a, b, StringComparison.OrdinalIgnoreCase) is var order and not 0
            ? order : string.CompareOrdinal(a, b));

    private sealed class Dir(string name, Dir? parent)
    {
        public string Name { get; } = name;
        public Dir? Parent { get; } = parent;
        public SortedDictionary<string, Dir> Dirs { get; } = new(_NAME_ORDER);
        public SortedDictionary<string, byte[]> Files { get; } = new(_NAME_ORDER);
        public uint Offset { get; set; }
        public uint FirstFile { get; set; } = _UNUSED;
    }

    private static byte[] BuildLevel3(IReadOnlyDictionary<string, byte[]> files)
    {
        var root = new Dir("", null);
        foreach (var (path, data) in files)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) throw new ArgumentException($"Invalid RomFS path: {path}");
            var node = root;
            foreach (var part in parts[..^1])
            {
                if (!node.Dirs.TryGetValue(part, out var next)) node.Dirs[part] = next = new(part, node);
                node = next;
            }
            node.Files[parts[^1]] = data;
        }

        // Directory entries breadth-first and files depth-first, like makerom.
        var dirs = new List<Dir>();
        var queue = new Queue<Dir>([root]);
        uint dirMetaSize = 0;
        while (queue.TryDequeue(out var node))
        {
            dirs.Add(node);
            node.Offset = dirMetaSize;
            dirMetaSize += EntrySize(node.Name, _DIR_ENTRY_SIZE);
            foreach (var child in node.Dirs.Values) queue.Enqueue(child);
        }
        var fileList = new List<(Dir Parent, string Name, byte[] Data, uint Offset, long DataOffset)>();
        uint fileMetaSize = 0;
        long dataSize = 0;
        var depthFirst = new Stack<Dir>([root]);
        while (depthFirst.TryPop(out var node))
        {
            foreach (var child in node.Dirs.Values.Reverse()) depthFirst.Push(child);
            foreach (var (name, data) in node.Files)
            {
                if (node.FirstFile == _UNUSED) node.FirstFile = fileMetaSize;
                // Empty files get offset 0 and take no data space, like makerom.
                var offset = data.Length == 0 ? 0 : Align(dataSize, 0x10);
                fileList.Add((node, name, data, fileMetaSize, offset));
                fileMetaSize += EntrySize(name, _FILE_ENTRY_SIZE);
                if (data.Length > 0) dataSize = offset + data.Length;
            }
        }

        var dirHash = new uint[BucketCount(dirs.Count)];
        var fileHash = new uint[BucketCount(fileList.Count)];
        Array.Fill(dirHash, _UNUSED);
        Array.Fill(fileHash, _UNUSED);
        var dirMeta = new byte[dirMetaSize];
        foreach (var node in dirs)
        {
            var parent = node.Parent?.Offset ?? 0;
            var sibling = node.Parent is { } owner
                ? owner.Dirs.Values.SkipWhile(dir => dir != node).Skip(1).FirstOrDefault()?.Offset
                    ?? _UNUSED
                : _UNUSED;
            var child = node.Dirs.Values.FirstOrDefault()?.Offset ?? _UNUSED;
            var bucket = HashName(parent, node.Name) % (uint)dirHash.Length;
            var rawName = Encoding.Unicode.GetBytes(node.Name);
            var entry = dirMeta.AsSpan((int)node.Offset);
            WriteWords(entry, parent, sibling, child, node.FirstFile, dirHash[bucket],
                (uint)rawName.Length);
            rawName.CopyTo(entry[_DIR_ENTRY_SIZE..]);
            dirHash[bucket] = node.Offset;
        }
        var fileMeta = new byte[fileMetaSize];
        for (var i = 0; i < fileList.Count; i++)
        {
            var (parent, name, data, offset, dataOffset) = fileList[i];
            var sibling = i + 1 < fileList.Count && fileList[i + 1].Parent == parent
                ? fileList[i + 1].Offset : _UNUSED;
            var bucket = HashName(parent.Offset, name) % (uint)fileHash.Length;
            var rawName = Encoding.Unicode.GetBytes(name);
            var entry = fileMeta.AsSpan((int)offset);
            WriteWords(entry, parent.Offset, sibling);
            BinaryPrimitives.WriteInt64LittleEndian(entry[0x08..], dataOffset);
            BinaryPrimitives.WriteInt64LittleEndian(entry[0x10..], data.Length);
            WriteWords(entry[0x18..], fileHash[bucket], (uint)rawName.Length);
            rawName.CopyTo(entry[_FILE_ENTRY_SIZE..]);
            fileHash[bucket] = offset;
        }

        var dirHashOffset = _LEVEL3_HEADER_SIZE;
        var dirMetaOffset = dirHashOffset + dirHash.Length * 4;
        var fileHashOffset = dirMetaOffset + dirMeta.Length;
        var fileMetaOffset = fileHashOffset + fileHash.Length * 4;
        var dataOffsetStart = Align(fileMetaOffset + fileMeta.Length, 0x10);
        var level3 = new byte[dataOffsetStart + dataSize];
        WriteWords(level3, _LEVEL3_HEADER_SIZE, (uint)dirHashOffset, (uint)dirHash.Length * 4,
            (uint)dirMetaOffset, (uint)dirMeta.Length, (uint)fileHashOffset,
            (uint)fileHash.Length * 4, (uint)fileMetaOffset, (uint)fileMeta.Length,
            (uint)dataOffsetStart);
        WriteWords(level3.AsSpan(dirHashOffset), dirHash);
        dirMeta.CopyTo(level3, dirMetaOffset);
        WriteWords(level3.AsSpan(fileHashOffset), fileHash);
        fileMeta.CopyTo(level3, fileMetaOffset);
        foreach (var file in fileList) file.Data.CopyTo(level3, dataOffsetStart + file.DataOffset);
        return level3;
    }

    // SHA-256 of every 0x1000 block, the last one zero-padded.
    private static byte[] HashLevel(byte[] data)
    {
        var blocks = (data.Length + _BLOCK - 1) / _BLOCK;
        var hashes = new byte[blocks * SHA256.HashSizeInBytes];
        var padded = new byte[_BLOCK];
        for (var i = 0; i < blocks; i++)
        {
            var block = data.AsSpan(i * _BLOCK, Math.Min(_BLOCK, data.Length - i * _BLOCK));
            if (block.Length < _BLOCK)
            {
                block.CopyTo(padded);
                block = padded;
            }
            SHA256.HashData(block, hashes.AsSpan(i * SHA256.HashSizeInBytes));
        }
        return hashes;
    }

    private static uint HashName(uint parent, string name)
    {
        var value = parent ^ 123456789u;
        foreach (var unit in name) value = ((value >> 5) | (value << 27)) ^ unit;
        return value;
    }

    private static int BucketCount(int entries)
    {
        if (entries < 3) return 3;
        if (entries < 19) return entries | 1;
        var count = entries;
        while (((int[])[2, 3, 5, 7, 11, 13, 17]).Any(prime => count % prime == 0)) count++;
        return count;
    }

    private static uint EntrySize(string name, int fixedSize) =>
        (uint)(fixedSize + Align(Encoding.Unicode.GetByteCount(name), 4));

    private static string Name(ReadOnlySpan<byte> entry, int fixedSize, int lengthField) =>
        Encoding.Unicode.GetString(entry.Slice(fixedSize, (int)ReadUInt32(entry, lengthField)));

    private static void PadTo(MemoryStream stream, int alignment) =>
        stream.Write(new byte[Align(stream.Length, alignment) - stream.Length]);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static void WriteWords(Span<byte> target, params ReadOnlySpan<uint> words)
    {
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(target[(i * 4)..], words[i]);
    }

    private static int Align(int value, int alignment) =>
        (value + alignment - 1) / alignment * alignment;

    private static long Align(long value, long alignment) =>
        (value + alignment - 1) / alignment * alignment;
}
