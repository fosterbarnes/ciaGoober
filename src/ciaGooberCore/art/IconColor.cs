using SkiaSharp;

namespace CiaGoober.Core;

// Icon background fill as #RRGGBB.
public static class IconColor
{
    private const string _HEX_DIGITS = "0123456789ABCDEFabcdef";

    public static bool IsValid(string? value) => value is { Length: 7 } && value[0] == '#'
        && !value.AsSpan(1).ContainsAnyExcept(_HEX_DIGITS);

    public static string Normalize(string value) => IsValid(value) ? value.ToUpperInvariant()
        : throw new ArgumentException($"Icon background must be #RRGGBB: '{value}'", nameof(value));

    internal static SKColor Parse(string value) =>
        new(Convert.ToUInt32(Normalize(value)[1..], 16) | 0xFF000000);
}
