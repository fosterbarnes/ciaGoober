namespace CiaGoober.Core;

// One place for every folder the app reads or writes. App data is per user; bundled files sit
// beside the executable (copied there from .res by the app project).
public static class AppPaths
{
    public static string AppData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ciaGoober");

    public static string Settings { get; } = Path.Combine(AppData, "settings.json");
    public static string Registry { get; } = Path.Combine(AppData, "forwarders.json");
    public static string GbaTemplate { get; } = Path.Combine(AppData, "gbaTemplate");
    public static string GbTemplate { get; } = Path.Combine(AppData, "gbTemplate");
    public static string NesTemplate { get; } = Path.Combine(AppData, "nesTemplate");
    public static string SnesTemplate { get; } = Path.Combine(AppData, "snesTemplate");
    public static string LaunchBox { get; } = Path.Combine(AppData, "launchbox");
    public static string LaunchBoxImages { get; } = Path.Combine(LaunchBox, "images");
    public static string LaunchBoxThumbs { get; } = Path.Combine(LaunchBox, "thumbs");
    public static string Logs { get; } = Path.Combine(AppData, "logs");
    public static string Jobs { get; } = Path.Combine(AppData, "jobs");
    public static string SdBackup { get; } = Path.Combine(AppData, "sdBackup");
    public static string RegistryBackup { get; } = Path.Combine(AppData, "registryBackup");

    public static string DefaultCiaOutput { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ciaGoober", "cia");

    public static string Tools { get; } = Path.Combine(AppContext.BaseDirectory, "tools");
    public static string Sounds { get; } = Path.Combine(AppContext.BaseDirectory, "ogg");
    public static string SdFiles { get; } = Path.Combine(AppContext.BaseDirectory, "sd");
    public static string BundledLaunchBox { get; } = Path.Combine(AppContext.BaseDirectory, "launchbox");

    // Template folder of a system that runs on a Virtual Console emulator from a donor.
    public static string VcTemplate(GameSystem system) => system switch
    {
        GameSystem.Gb => GbTemplate,
        GameSystem.Nes => NesTemplate,
        GameSystem.Snes => SnesTemplate,
        _ => throw new ArgumentOutOfRangeException(nameof(system), system, null),
    };
}
