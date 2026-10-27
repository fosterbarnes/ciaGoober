namespace CiaGoober.Core;

public sealed record GbaChoice(string Value, string Label);

// GbaOptions values and their UI labels.
public static class GbaChoices
{
    public const string DEFAULT_SAVE_TYPE = "auto";
    public const string DEFAULT_SCREEN = "corrected";
    public const string DEFAULT_GHOSTING = "default";

    public static IReadOnlyList<GbaChoice> SaveTypes { get; } =
        [new("auto", "Auto"), .. Enum.GetValues<SaveType>().Select(type => new GbaChoice(
            type.ToString(), type.Label()))];

    public static IReadOnlyList<GbaChoice> Screens { get; } =
        [new("corrected", "Color Corrected"), new("bright", "Bright"), new("vc", "Original VC")];

    public static IReadOnlyList<GbaChoice> Ghosting { get; } =
        [
            new("off", "Off"), new("light", "Light"), new("default", "Default"),
            new("strong", "Strong"),
        ];
}
