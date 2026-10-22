using System.Text.Json;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.EnvMise.Host;

/// <summary>Applies <c>mise env --json</c> to a workspace's shell: the tool versions' PATH and the <c>[env]</c> table of its mise configuration.</summary>
public sealed class EnvMiseHost : PluginHostModule
{
    internal static readonly string[] Configuration =
        ["mise.toml", ".mise.toml", "mise.local.toml", ".mise.local.toml", ".tool-versions", ".config/mise.toml", ".config/mise/config.toml", ".mise/config.toml"];

    public override PluginContract Contract => EnvMiseContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        context.WorkspaceEnvironment(EnvMiseContract.Order, ApplyAsync);
        return ValueTask.FromResult<PluginDisposer?>(null);
    }

    internal static async ValueTask ApplyAsync(IWorkspaceEnvironmentEdit edit, CancellationToken cancellationToken)
    {
        if (!Configuration.Any(file => File.Exists(Path.Combine(edit.Directory, file)))) return;
        if (Locate(edit) is not { } mise)
        {
            edit.Log.Warn($"{edit.Directory} has mise configuration but mise is not installed. Install it (for example `brew install mise`) to get its tools.");
            edit.Report("mise missing", "This workspace has mise configuration but mise is not installed. Install it, for example with `brew install mise`.");
            return;
        }
        // The shell keeps the environment in sync as it changes directory, once the hook is in. The absolute path
        // works for an app started with the minimal PATH of a launcher.
        var quoted = "'" + mise.Replace("'", "'\\''") + "'";
        edit.ShellInit("zsh", $"[ -x {quoted} ] && eval \"$({quoted} activate zsh)\"");
        edit.ShellInit("fish", $"test -x {quoted}; and {quoted} activate fish | source");
        var result = await edit.RunAsync(mise, ["env", "--json"], null, cancellationToken);
        if (!result.Succeeded && result.Error.Contains("not trusted", StringComparison.OrdinalIgnoreCase))
        {
            if (!await TryInheritTrust(edit, mise, cancellationToken))
            {
                edit.Log.Warn($"The mise configuration in {edit.Directory} is not trusted. Run `mise trust` there to apply it.");
                edit.Report("mise untrusted", $"Run `mise trust` in {edit.Directory} to apply its mise configuration.");
                return;
            }
            result = await edit.RunAsync(mise, ["env", "--json"], null, cancellationToken);
        }
        if (!result.Succeeded)
        {
            var reason = result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no output";
            edit.Log.Warn($"`mise env --json` failed in {edit.Directory}: {reason}");
            edit.Report("mise failed", reason);
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null) edit.Set(property.Name, property.Value.GetString());
            edit.Report("mise", "The tools and [env] of this workspace's mise configuration");
        }
        catch (JsonException error) { edit.Log.Warn("Unreadable `mise env --json` output: " + error.Message); }
    }

    /// <summary>Where mise is looked for besides PATH: an app started from the dock or Finder has only the system's PATH.</summary>
    internal static string[] SearchDirectories { get; set; } = ["/opt/homebrew/bin", "/usr/local/bin", "/home/linuxbrew/.linuxbrew/bin", "/opt/local/bin"];

    private static string? Locate(IWorkspaceEnvironmentEdit edit)
    {
        if (edit.Find("mise") is { } found) return found;
        if (edit.Get("MISE_INSTALL_PATH") is { Length: > 0 } installed && File.Exists(installed)) return installed;
        var home = edit.Get("HOME") ?? edit.Get("USERPROFILE") ?? "";
        string[] user = home.Length == 0 ? [] : [Path.Combine(home, ".local", "bin"), Path.Combine(home, ".local", "share", "mise", "bin"), Path.Combine(home, ".cargo", "bin")];
        return SearchDirectories.Concat(user).Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "mise.exe" : "mise")).FirstOrDefault(File.Exists);
    }

    // A worktree inherits the default workspace's decision only for byte-identical configuration that mise already accepts there.
    private static async ValueTask<bool> TryInheritTrust(IWorkspaceEnvironmentEdit edit, string mise, CancellationToken cancellationToken)
    {
        if (edit.DefaultDirectory is not { } origin || !edit.MatchesDefault(Configuration)) return false;
        if (!(await edit.RunAsync(mise, ["env", "--json"], origin, cancellationToken)).Succeeded) return false;
        var granted = await edit.RunAsync(mise, ["trust"], null, cancellationToken);
        if (granted.Succeeded) edit.Log.Info($"Trusted {edit.Directory}, whose mise configuration matches the project's default workspace.");
        return granted.Succeeded;
    }
}