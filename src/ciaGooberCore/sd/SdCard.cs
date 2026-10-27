using System.Globalization;
using System.Security.Cryptography;

namespace CiaGoober.Core;

public enum ReceiverState { Current, Different, Missing }

public sealed record ReceiverStatus(string FileName, ReceiverState State);

// One drive: HasTwilight = <root>\_nds\TWiLightMenu exists; Receivers is empty without it.
// Error is set when the drive could not be read.
public sealed record SdCardInfo(string Root, string? Label, bool HasTwilight,
    IReadOnlyList<ReceiverStatus> Receivers, string? Error)
{
    public bool AllCurrent => HasTwilight && Receivers.All(r => r.State == ReceiverState.Current);

    public string Summary =>
        Error is not null ? "unreadable"
        : !HasTwilight ? "no TWiLight Menu++"
        : AllCurrent ? "receivers current"
        : Receivers.All(r => r.State != ReceiverState.Different) ? "receivers missing"
        : "receivers differ";
}

// The modified TWiLight Menu++ receivers on the SD card (nds README "Current SD deployment").
// Cards are drive roots other than the system drive; installs only go to cards that already
// hold _nds\TWiLightMenu, since the receivers replace parts of TWiLight Menu++.
public static class SdCard
{
    public static readonly IReadOnlyList<string> RECEIVERS = ["main.srldr", "dsimenu.srldr"];
    public static readonly string TWILIGHT_FOLDER = Path.Combine("_nds", "TWiLightMenu");

    // File name -> SHA-256 (uppercase hex) of the bundled receivers.
    public static IReadOnlyDictionary<string, string> BundledHashes(string bundledFolder) =>
        RECEIVERS.ToDictionary(name => name, name =>
        {
            var path = Path.Combine(bundledFolder, name);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Bundled receiver is missing: {path}");
            return Hash(path);
        });

    // Every ready removable drive except the system drive, with or without TWiLight Menu++.
    public static IReadOnlyList<SdCardInfo> FindCards(string bundledFolder)
    {
        var bundled = BundledHashes(bundledFolder);
        return DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Removable && drive.IsReady
                && !IsSystemDrive(drive.RootDirectory.FullName))
            .Select(drive => Inspect(drive.RootDirectory.FullName, bundled))
            .ToList();
    }

    public static SdCardInfo Inspect(string cardRoot, string bundledFolder) =>
        Inspect(cardRoot, BundledHashes(bundledFolder));

    // True for C: and the drive Windows runs from (also for any path on them).
    public static bool IsSystemDrive(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        return string.Equals(root, @"C:\", StringComparison.OrdinalIgnoreCase)
            || string.Equals(root, Path.GetPathRoot(Environment.SystemDirectory),
                StringComparison.OrdinalIgnoreCase);
    }

    public static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // Copies both receivers to <cardRoot>\_nds\TWiLightMenu. Files it replaces are first copied
    // to backupRoot\<yyyyMMdd-HHmmss>\_nds\TWiLightMenu; each new file goes through a temp name
    // and is hash-checked. On any failure every receiver is put back as it was and this throws.
    // Returns the backup folder, or null when no file was replaced.
    public static string? Install(string cardRoot, string bundledFolder, string backupRoot)
    {
        if (IsDriveRoot(cardRoot) && IsSystemDrive(cardRoot))
            throw new InvalidOperationException($"{cardRoot} is the system drive, not an SD card.");
        var twilight = Path.Combine(cardRoot, TWILIGHT_FOLDER);
        if (!Directory.Exists(twilight))
            throw new InvalidOperationException(
                $"{cardRoot} has no {TWILIGHT_FOLDER}; install TWiLight Menu++ on the card first.");
        var bundled = BundledHashes(bundledFolder);
        // Original hash per receiver (null = missing); current files are left alone.
        var plan = RECEIVERS
            .Select(name => (Name: name, Target: Path.Combine(twilight, name)))
            .Select(item => (item.Name, item.Target,
                Original: File.Exists(item.Target) ? Hash(item.Target) : null))
            .Where(item => item.Original != bundled[item.Name])
            .ToList();
        if (plan.Count == 0) return null;

        string? backup = null;
        if (plan.Any(item => item.Original is not null))
        {
            backup = NewBackupFolder(backupRoot);
            var backupTwilight = Path.Combine(backup, TWILIGHT_FOLDER);
            Directory.CreateDirectory(backupTwilight);
            foreach (var item in plan.Where(item => item.Original is not null))
            {
                var copy = Path.Combine(backupTwilight, item.Name);
                File.Copy(item.Target, copy);
                if (Hash(copy) != item.Original)
                    throw new IOException($"Backup of {item.Target} does not match the card file.");
            }
        }

        var temporary = new List<string>();
        try
        {
            foreach (var item in plan)
            {
                var staged = Path.Combine(twilight, $".{item.Name}.{Guid.NewGuid():N}.tmp");
                temporary.Add(staged);
                File.Copy(Path.Combine(bundledFolder, item.Name), staged);
                VerifyHash(staged, bundled[item.Name]);
                File.Move(staged, item.Target, true);
                VerifyHash(item.Target, bundled[item.Name]);
            }
            return backup;
        }
        catch (Exception error)
        {
            try
            {
                foreach (var item in plan) Restore(item.Target, item.Original, backup, item.Name);
            }
            catch (Exception restoreError)
            {
                throw new IOException($"Receiver install failed and the card could not be restored "
                    + $"(backup: {backup ?? "none"}): {error.Message}; {restoreError.Message}",
                    new AggregateException(error, restoreError));
            }
            throw new IOException(
                $"Receiver install failed; the card was restored: {error.Message}", error);
        }
        finally
        {
            // A pulled card must not replace the install or restore message.
            foreach (var path in temporary) Cleanup.TryDeleteFile(path);
        }
    }

    private static SdCardInfo Inspect(string cardRoot, IReadOnlyDictionary<string, string> bundled)
    {
        string? label = null;
        try
        {
            if (IsDriveRoot(cardRoot)) label = new DriveInfo(cardRoot).VolumeLabel;
            var twilight = Path.Combine(cardRoot, TWILIGHT_FOLDER);
            if (!Directory.Exists(twilight)) return new(cardRoot, label, false, [], null);
            var receivers = RECEIVERS.Select(name =>
            {
                var path = Path.Combine(twilight, name);
                var state = !File.Exists(path) ? ReceiverState.Missing
                    : Hash(path) == bundled[name] ? ReceiverState.Current
                    : ReceiverState.Different;
                return new ReceiverStatus(name, state);
            }).ToList();
            return new(cardRoot, label, true, receivers, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(cardRoot, label, false, [], error.Message);
        }
    }

    private static bool IsDriveRoot(string path) =>
        string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase);

    private static string NewBackupFolder(string backupRoot)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var folder = Path.Combine(backupRoot, stamp);
        for (var n = 2; Directory.Exists(folder); n++)
            folder = Path.Combine(backupRoot, $"{stamp}-{n}");
        return folder;
    }

    private static void VerifyHash(string path, string expected)
    {
        if (Hash(path) != expected)
            throw new IOException($"Copied file does not match the bundled receiver: {path}");
    }

    // Puts one receiver back to its original content; files already in that state are untouched.
    private static void Restore(string target, string? original, string? backup, string name)
    {
        if (original is null)
        {
            File.Delete(target);
            return;
        }
        if (File.Exists(target) && Hash(target) == original) return;
        File.Copy(Path.Combine(backup!, TWILIGHT_FOLDER, name), target, true);
        if (Hash(target) != original)
            throw new IOException($"Restored file does not match its backup: {target}");
    }
}
