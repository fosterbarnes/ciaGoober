using System.IO;
using System.Text;
using System.Text.Json;

namespace CiaGoober.ViewModels;

// settings.json shape. Missing fields read as null and take the default.
public sealed record SettingsData(string? CiaOutputFolder, string? PcDsRomFolder,
    string? PcGbaRomFolder, string? PcGbRomFolder, string? SdRomFolder,
    string? DefaultIconBackground, int? ParallelBuilds, string? SelectedPage,
    string? PcNesRomFolder);

public static class SettingsStore
{
    private static readonly JsonSerializerOptions _JSON = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly UTF8Encoding _UTF8 = new(false);

    // A missing file gives defaults silently; an unreadable one gives defaults plus Problem, and
    // stays on disk until the next save replaces it.
    public static (SettingsData Data, string? Problem) Load(string path)
    {
        var empty = new SettingsData(null, null, null, null, null, null, null, null, null);
        if (!File.Exists(path)) return (empty, null);
        try
        {
            var data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(path), _JSON)
                ?? throw new JsonException("The file holds no settings object.");
            return (data, null);
        }
        catch (Exception error) when (error is JsonException or IOException
            or UnauthorizedAccessException)
        {
            return (empty, $"Settings file could not be read, defaults are in use and it is "
                + $"replaced on the next settings change: {path} ({error.Message})");
        }
    }

    // Temp file in the same folder, flushed, then moved over the old file.
    public static void Save(string path, SettingsData data)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(_UTF8.GetBytes(JsonSerializer.Serialize(data, _JSON) + "\n"));
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
