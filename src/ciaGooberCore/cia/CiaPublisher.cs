using System.Text;

namespace CiaGoober.Core;

internal static class CiaPublisher
{
    // Ours = the TMD unique ID matches and, for DS (sdPath set), the file holds the SD path bytes
    // (generator.py:518-520, 752-753). A file CiaReader rejects is not ours.
    public static bool Owns(string ciaPath, int uniqueId, string? sdPath)
    {
        try
        {
            if (CiaReader.UniqueId(ciaPath) != uniqueId) return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        return sdPath is null
            || File.ReadAllBytes(ciaPath).AsSpan().IndexOf(Encoding.UTF8.GetBytes(sdPath)) >= 0;
    }

    // Copies the built CIA over the output without ever leaving it missing or partial: stage a
    // hidden sibling, back up the old file, rename over it, and restore the backup if a failed
    // rename left the output missing.
    public static void Publish(string builtPath, string outputPath, int uniqueId, string? sdPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var exists = File.Exists(outputPath);
        if (exists && !Owns(outputPath, uniqueId, sdPath))
            throw new IOException($"Refusing to overwrite unrelated CIA: {outputPath}");
        var staged = HiddenSibling(outputPath);
        var backup = exists ? HiddenSibling(outputPath) : null;
        try
        {
            CopyHidden(builtPath, staged);
            if (backup is not null) CopyHidden(outputPath, backup);
            File.SetAttributes(staged, FileAttributes.Normal);
            try
            {
                File.Move(staged, outputPath, true);
            }
            catch when (backup is not null && !File.Exists(outputPath))
            {
                File.SetAttributes(backup, FileAttributes.Normal);
                File.Move(backup, outputPath, true);
                throw;
            }
        }
        finally
        {
            Cleanup.TryDeleteFile(staged);
            // A failed restore leaves the output missing; the backup is then the only copy.
            if (backup is not null && File.Exists(outputPath)) Cleanup.TryDeleteFile(backup);
        }
    }

    private static string HiddenSibling(string path) => Path.Combine(Path.GetDirectoryName(path)!,
        $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

    private static void CopyHidden(string source, string destination)
    {
        File.Copy(source, destination);
        File.SetAttributes(destination, FileAttributes.Hidden);
    }
}
