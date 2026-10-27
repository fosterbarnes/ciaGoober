namespace CiaGoober.Core;

// Cleanup that runs after an outcome is decided and must not replace it with its own error.
internal static class Cleanup
{
    public static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
