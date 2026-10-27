namespace CiaGoober.Core;

// Template folders in app data (GBA, GB/GBC, NES), replaced as a whole.
internal static class TemplateFolder
{
    // Written to a sibling folder first so a failed write never looks installed.
    public static void Replace(string directory, IEnumerable<(string Name, byte[] Data)> files)
    {
        directory = Path.GetFullPath(directory);
        var staging = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(directory)!,
            $".{Path.GetFileName(directory)}.{Guid.NewGuid():N}")).FullName;
        try
        {
            foreach (var (name, data) in files) File.WriteAllBytes(Path.Combine(staging, name), data);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            Directory.Move(staging, directory);
        }
        finally
        {
            DeleteStaging(staging);
        }
    }

    // Removes an installed template; a missing folder is already removed.
    public static void Delete(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    // Best-effort like the original's rmtree(ignore_errors): never hides the save's own error.
    private static void DeleteStaging(string staging)
    {
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
