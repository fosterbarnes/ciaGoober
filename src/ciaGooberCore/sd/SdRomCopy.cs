namespace CiaGoober.Core;

// Copies a ROM to the SD card through a temp file in the target folder, so a pulled card or a
// cancel never leaves a partial ROM under the real name. Never replaces an existing file.
public static class SdRomCopy
{
    private const int _BUFFER_SIZE = 1 << 20;

    // Progress reports bytes copied so far.
    public static async Task CopyAsync(string source, string target, IProgress<long> progress,
        CancellationToken ct)
    {
        if (File.Exists(target)) throw new IOException($"A file is already at {target}");
        var folder = Path.GetDirectoryName(Path.GetFullPath(target))!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.Read, _BUFFER_SIZE, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, _BUFFER_SIZE, true))
            {
                var buffer = new byte[_BUFFER_SIZE];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copied += read;
                    progress.Report(copied);
                }
                output.Flush(true);
            }
            File.Move(temporary, target, false);
        }
        finally
        {
            if (File.Exists(temporary)) Cleanup.TryDeleteFile(temporary);
        }
    }
}
