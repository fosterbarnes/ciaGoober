using System.Diagnostics;

namespace CiaGoober.Core;

internal static class ToolRunner
{
    // Runs a bundled tool (AppPaths.Tools\<tool>.exe) in the job folder. makerom and bannertool
    // read an ANSI argv, so arguments must be ASCII names relative to the job folder.
    public static async Task RunAsync(string tool, string jobFolder, IEnumerable<string> arguments,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(AppPaths.Tools, tool + ".exe"))
        {
            WorkingDirectory = jobFolder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {tool}.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        string[] streams = [await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false)];
        var output = string.Join("\n", streams.Select(text => text.Trim()).Where(text => text != ""));
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{tool} failed with exit code {process.ExitCode}"
                + (output == "" ? "." : $":\n{output}"));
    }
}
