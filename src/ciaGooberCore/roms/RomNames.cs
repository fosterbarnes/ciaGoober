using System.Text.RegularExpressions;

namespace CiaGoober.Core;

// Card names for GBA and GB ROMs, whose headers hold only a short uppercase title.
internal static partial class RomNames
{
    private const int _MAX_LENGTH = 64;

    // The No-Intro file name without "(USA)"-style tags, else the header title, else the system
    // name; at most 64 UTF-16 units without splitting a surrogate pair.
    public static string FromFileName(string path, string headerTitle, string systemName)
    {
        var name = TagGroups().Replace(Path.GetFileNameWithoutExtension(path), "").Trim();
        name = name.Length > 0 ? name : headerTitle.Length > 0 ? headerTitle : systemName;
        if (name.Length <= _MAX_LENGTH) return name;
        var cut = char.IsHighSurrogate(name[_MAX_LENGTH - 1]) ? _MAX_LENGTH - 1 : _MAX_LENGTH;
        return name[..cut];
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex TagGroups();
}
