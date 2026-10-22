using System.Text.Json;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.EnvDirenv.Host;

/// <summary>Applies <c>direnv export json</c> for a workspace's <c>.envrc</c> to its shell, after mise so <c>use mise</c> sees the same tools.</summary>
public sealed class EnvDirenvHost : PluginHostModule
{
    private const string Rc = ".envrc";
    private const int Allowed = 0;
    private const int NotAllowed = 1;

    public override PluginContract Contract => EnvDirenvContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        context.WorkspaceEnvironment(EnvDirenvContract.Order, ApplyAsync);
        return ValueTask.FromResult<PluginDisposer?>(null);
    }

    internal static async ValueTask ApplyAsync(IWorkspaceEnvironmentEdit edit, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(edit.Directory, Rc))) return;
        if (Locate(edit) is not { } direnv)
        {
            edit.Log.Warn($"{edit.Directory} has an .envrc but direnv is not installed. Install it (for example `brew install direnv`) to apply it.");
            edit.Report("direnv missing", "This workspace has an .envrc but direnv is not installed. Install it, for example with `brew install direnv`.");
            return;
        }
        var quoted = "'" + direnv.Replace("'", "'\\''") + "'";
        edit.ShellInit("zsh", $"[ -x {quoted} ] && eval \"$({quoted} hook zsh)\"");
        edit.ShellInit("fish", $"test -x {quoted}; and {quoted} hook fish | source");
        var allowed = await AllowedAsync(edit, direnv, null, cancellationToken);
        if (allowed == NotAllowed)
        {
            if (!await TryInheritTrust(edit, direnv, cancellationToken))
            {
                edit.Log.Warn($"{Path.Combine(edit.Directory, Rc)} is blocked. Run `direnv allow` there to apply it.");
                edit.Report("direnv blocked", $"Run `direnv allow` in {edit.Directory} to apply its .envrc.");
                return;
            }
        }
        else if (allowed is not null && allowed != Allowed) return;
        var result = await edit.RunAsync(direnv, ["export", "json"], null, cancellationToken);
        if (!result.Succeeded)
        {
            var reason = result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no output";
            edit.Log.Warn($"`direnv export json` failed in {edit.Directory}: {reason}");
            edit.Report("direnv failed", reason);
            return;
        }
        if (string.IsNullOrWhiteSpace(result.Output)) return;
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null) edit.Set(property.Name, property.Value.GetString());
            edit.Report("direnv", "The environment of this workspace's .envrc");
        }
        catch (JsonException error) { edit.Log.Warn("Unreadable `direnv export json` output: " + error.Message); }
    }

    /// <summary>Where direnv is looked for besides PATH: an app started from the dock or Finder has only the system's PATH.</summary>
    internal static string[] SearchDirectories { get; set; } = ["/opt/homebrew/bin", "/usr/local/bin", "/home/linuxbrew/.linuxbrew/bin", "/opt/local/bin"];

    private static string? Locate(IWorkspaceEnvironmentEdit edit)
    {
        if (edit.Find("direnv") is { } found) return found;
        var home = edit.Get("HOME") ?? edit.Get("USERPROFILE") ?? "";
        string[] user = home.Length == 0 ? [] : [Path.Combine(home, ".local", "bin"), Path.Combine(home, ".nix-profile", "bin")];
        return SearchDirectories.Concat(user).Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "direnv.exe" : "direnv")).FirstOrDefault(File.Exists);
    }

    /// <summary>direnv's verdict on the .envrc found from the directory (0 allowed, 1 not allowed, 2 denied), or null when it cannot say.</summary>
    private static async ValueTask<int?> AllowedAsync(IWorkspaceEnvironmentEdit edit, string direnv, string? directory, CancellationToken cancellationToken)
    {
        var result = await edit.RunAsync(direnv, ["status", "--json"], directory, cancellationToken);
        if (!result.Succeeded) return null;
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            return document.RootElement.TryGetProperty("state", out var current) && current.TryGetProperty("foundRC", out var found)
                && found.ValueKind == JsonValueKind.Object && found.TryGetProperty("allowed", out var verdict) && verdict.TryGetInt32(out var value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    // A worktree inherits the default workspace's decision only for a byte-identical .envrc that direnv already allows there.
    private static async ValueTask<bool> TryInheritTrust(IWorkspaceEnvironmentEdit edit, string direnv, CancellationToken cancellationToken)
    {
        if (edit.DefaultDirectory is not { } origin || !edit.MatchesDefault([Rc])) return false;
        if (await AllowedAsync(edit, direnv, origin, cancellationToken) != Allowed) return false;
        var granted = await edit.RunAsync(direnv, ["allow"], null, cancellationToken);
        if (granted.Succeeded) edit.Log.Info($"Allowed {Path.Combine(edit.Directory, Rc)}, which matches the project's default workspace.");
        return granted.Succeeded;
    }
}