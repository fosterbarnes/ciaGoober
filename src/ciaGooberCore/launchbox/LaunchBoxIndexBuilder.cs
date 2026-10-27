using System.IO.Compression;
using System.Text.Json;
using System.Xml;

namespace CiaGoober.Core;

public enum LaunchBoxRefreshStage { Download, Index }

// Total is 0 when the download size is unknown. Index bytes are uncompressed XML bytes read over
// both Metadata.xml passes and Files.xml.
public sealed record LaunchBoxRefreshProgress(LaunchBoxRefreshStage Stage, long Done, long Total);

// Port of launchbox.py: builds index.json (DS), indexGba.json (GBA), indexGb.json (GB and GBC),
// indexNes.json (NES) and indexSnes.json (SNES) from the daily dump.
public static class LaunchBoxIndexBuilder
{
    private const string _METADATA_URL = "https://gamesdb.launchbox-app.com/Metadata.zip";
    private static readonly TimeSpan _READ_TIMEOUT = TimeSpan.FromSeconds(60);
    private static readonly Dictionary<string, GameSystem> _PLATFORMS = new()
    {
        ["Nintendo DS"] = GameSystem.Nds,
        ["Nintendo Game Boy Advance"] = GameSystem.Gba,
        ["Nintendo Game Boy"] = GameSystem.Gb,
        ["Nintendo Game Boy Color"] = GameSystem.Gb,
        ["Nintendo Entertainment System"] = GameSystem.Nes,
        ["Super Nintendo Entertainment System"] = GameSystem.Snes,
    };

    // Downloads Metadata.zip to a temp file, writes the DS, GBA, GB, NES and SNES indexes into
    // AppPaths.LaunchBox and drops the cached indexes.
    public static async Task RefreshAsync(IProgress<LaunchBoxRefreshProgress>? progress,
        CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetTempPath(),
            $"ciaGooberMetadata.{Environment.ProcessId}.{Guid.NewGuid():N}.zip");
        await using var archive = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        await Download(archive, progress, cancellationToken);
        archive.Position = 0;
        await Task.Run(() => BuildFromArchive(archive, AppPaths.LaunchBox, progress,
            cancellationToken), cancellationToken);
        LaunchBoxIndex.ClearCache();
    }

    // Two passes over Metadata.xml keep only wanted records whatever their order
    // (launchbox.py buildIndexes), then Files.xml maps No-Intro names; each index is written
    // through a temp file.
    public static void BuildFromArchive(Stream metadataZip, string folder,
        IProgress<LaunchBoxRefreshProgress>? progress, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(metadataZip, ZipArchiveMode.Read, leaveOpen: true);
        var metadata = Entry(zip, "Metadata.xml");
        var filesEntry = Entry(zip, "Files.xml");
        var reporter = new Reporter(progress, LaunchBoxRefreshStage.Index,
            2 * metadata.Length + filesEntry.Length);

        var games = new OrderedDictionary<string, IndexGame>();
        foreach (var (_, record) in Records(metadata, reporter, cancellationToken, "Game"))
        {
            if (!_PLATFORMS.TryGetValue(record.GetValueOrDefault("Platform", ""), out var system))
                continue;
            var id = record.GetValueOrDefault("DatabaseID", "");
            var name = record.GetValueOrDefault("Name", "");
            if (id.Length == 0 || name.Length == 0) continue;
            games[id] = new IndexGame(id, name, system, Year(record));
        }
        foreach (var (tag, record) in Records(metadata, reporter, cancellationToken, "GameImage",
            "GameAlternateName"))
        {
            if (!games.TryGetValue(record.GetValueOrDefault("DatabaseID", ""), out var game)) continue;
            if (tag == "GameImage")
            {
                var file = record.GetValueOrDefault("FileName", "");
                if (LaunchBoxImages.IsSafeName(file))
                    game.Images.Add([record.GetValueOrDefault("Type", ""),
                        record.GetValueOrDefault("Region", ""), file]);
                continue;
            }
            var alt = record.GetValueOrDefault("AlternateName", "");
            if (alt.Length > 0 && alt != game.Name && !game.Alt.Contains(alt)) game.Alt.Add(alt);
        }
        var files = _PLATFORMS.Values.Distinct().ToDictionary(system => system,
            _ => new OrderedDictionary<string, string>());
        foreach (var (_, record) in Records(filesEntry, reporter, cancellationToken, "File"))
        {
            var fileName = record.GetValueOrDefault("FileName", "");
            var gameName = record.GetValueOrDefault("GameName", "");
            if (_PLATFORMS.TryGetValue(record.GetValueOrDefault("Platform", ""), out var system)
                && fileName.Length > 0 && gameName.Length > 0)
                files[system][fileName] = gameName;
        }

        var withImages = games.Values.Where(game => game.Images.Count > 0).ToArray();
        if (!withImages.Any(game => game.System == GameSystem.Nds))
            throw new InvalidDataException("LaunchBox metadata contains no Nintendo DS images.");
        Directory.CreateDirectory(folder);
        foreach (var system in _PLATFORMS.Values.Distinct())
            WriteIndex(Path.Combine(folder, LaunchBoxIndex.FileName(system)),
                withImages.Where(game => game.System == system), files[system]);
    }

    private static async Task Download(Stream archive, IProgress<LaunchBoxRefreshProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await LaunchBoxDownloads.Http.GetAsync(_METADATA_URL,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"LaunchBox metadata download failed with HTTP {(int)response.StatusCode}",
                null, response.StatusCode);
        var total = response.Content.Headers.ContentLength ?? 0;
        var reporter = new Reporter(progress, LaunchBoxRefreshStage.Download, total);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[1 << 20];
        // HttpClient.Timeout stops at the headers here, so each read gets its own timeout.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            stall.CancelAfter(_READ_TIMEOUT);
            int read;
            try
            {
                read = await body.ReadAsync(buffer, stall.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HttpRequestException("LaunchBox metadata download stalled.");
            }
            if (read == 0) break;
            await archive.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            reporter.Advance(read);
        }
    }

    private static ZipArchiveEntry Entry(ZipArchive zip, string name) => zip.GetEntry(name)
        ?? throw new InvalidDataException($"LaunchBox Metadata.zip has no {name}.");

    // (tag, child element texts) for each wanted root child, one record in memory at a time.
    private static IEnumerable<(string Tag, Dictionary<string, string> Record)> Records(
        ZipArchiveEntry entry, Reporter reporter, CancellationToken cancellationToken,
        params string[] tags)
    {
        using var stream = new CountingStream(entry.Open(), reporter);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            IgnoreComments = true, IgnoreWhitespace = true, IgnoreProcessingInstructions = true,
        });
        reader.MoveToContent();
        if (reader.IsEmptyElement) yield break;
        reader.Read();
        while (!reader.EOF && reader.Depth >= 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || !tags.Contains(reader.LocalName))
            {
                reader.Skip();
                continue;
            }
            var tag = reader.LocalName;
            var record = new Dictionary<string, string>();
            if (reader.IsEmptyElement)
            {
                reader.Read();
                yield return (tag, record);
                continue;
            }
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Skip();
                    continue;
                }
                var name = reader.LocalName;
                record[name] = reader.IsEmptyElement
                    ? ReadEmpty(reader) : reader.ReadElementContentAsString().Trim();
            }
            reader.Read();
            yield return (tag, record);
        }
    }

    private static string ReadEmpty(XmlReader reader)
    {
        reader.Read();
        return "";
    }

    private static string? Year(Dictionary<string, string> record)
    {
        var year = record.GetValueOrDefault("ReleaseYear", "");
        if (year.Length > 0) return year;
        var date = record.GetValueOrDefault("ReleaseDate", "");
        return date.Length >= 4 && date[..4].All(char.IsAsciiDigit) ? date[..4] : null;
    }

    // Same JSON shape and key order as launchbox.py writeIndex.
    private static void WriteIndex(string path, IEnumerable<IndexGame> games,
        OrderedDictionary<string, string> files)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        try
        {
            using (var stream = File.Create(temporary))
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                json.WriteStartObject();
                json.WriteString("source", _METADATA_URL);
                json.WriteStartArray("games");
                foreach (var game in games)
                {
                    json.WriteStartObject();
                    json.WriteString("id", game.Id);
                    json.WriteString("name", game.Name);
                    json.WriteStartArray("images");
                    foreach (var image in game.Images)
                    {
                        json.WriteStartArray();
                        foreach (var value in image) json.WriteStringValue(value);
                        json.WriteEndArray();
                    }
                    json.WriteEndArray();
                    if (game.Year is not null) json.WriteString("year", game.Year);
                    if (game.Alt.Count > 0)
                    {
                        json.WriteStartArray("alt");
                        foreach (var alt in game.Alt) json.WriteStringValue(alt);
                        json.WriteEndArray();
                    }
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartObject("files");
                foreach (var (fileName, gameName) in files) json.WriteString(fileName, gameName);
                json.WriteEndObject();
                json.WriteEndObject();
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed record IndexGame(string Id, string Name, GameSystem System, string? Year)
    {
        public List<string[]> Images { get; } = [];
        public List<string> Alt { get; } = [];
    }

    // Reports only when the whole percentage (whole MB when the total is unknown) changes.
    private sealed class Reporter(IProgress<LaunchBoxRefreshProgress>? progress,
        LaunchBoxRefreshStage stage, long total)
    {
        private long _done;
        private long _lastStep = -1;

        public void Advance(int count)
        {
            _done += count;
            var step = total > 0 ? _done * 100 / total : _done >> 20;
            if (progress is null || step == _lastStep) return;
            _lastStep = step;
            progress.Report(new LaunchBoxRefreshProgress(stage, _done, total));
        }
    }

    private sealed class CountingStream(Stream inner, Reporter reporter) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            reporter.Advance(read);
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
