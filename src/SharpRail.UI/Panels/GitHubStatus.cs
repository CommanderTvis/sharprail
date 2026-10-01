using System.ComponentModel;
using System.Diagnostics;

namespace SharpRail.UI.Panels;

public sealed record GitHubStatus(bool Connected, string Detail);

public static class GitHubProbe
{
    public static async Task<GitHubStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("gh", ["auth", "status"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        try
        {
            using var process = Process.Start(start) ?? throw new Win32Exception("gh could not be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = (await output + await error).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "";
            return process.ExitCode == 0 ? new(true, text) : new(false, "gh is installed but not signed in. Run gh auth login.");
        }
        catch (Win32Exception) { return new(false, "The gh command-line tool was not found on PATH."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, "gh did not respond."); }
    }
}