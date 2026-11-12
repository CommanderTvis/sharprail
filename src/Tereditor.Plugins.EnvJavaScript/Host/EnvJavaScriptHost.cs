using System.Text.Json;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.EnvJavaScript.Host;

/// <summary>
/// Puts a workspace's JavaScript tooling on PATH for the main runtimes: <c>node_modules/.bin</c> (npm, pnpm, yarn's
/// node-modules linker and bun), the Node version a <c>.nvmrc</c> or <c>.node-version</c> names from nvm (including its
/// aliases) or fnm, Volta's
/// shims for a <c>package.json</c> that pins a toolchain, and Bun's or Deno's own install directory for a project that
/// uses them. It never installs anything.
/// </summary>
public sealed class EnvJavaScriptHost : PluginHostModule
{
    public override PluginContract Contract => EnvJavaScriptContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        context.WorkspaceEnvironmentStep(EnvJavaScriptContract.StepId, Apply);
        return ValueTask.FromResult<PluginDisposer?>(null);
    }

    internal static ValueTask Apply(IWorkspaceEnvironmentEdit edit, CancellationToken cancellationToken)
    {
        var home = edit.Get("HOME") ?? edit.Get("USERPROFILE") ?? "";
        // Runtimes first, so the project's own binaries end up ahead of them.
        if (Marked(edit, "deno.json", "deno.jsonc", "deno.lock") && Existing(Path.Combine(edit.Get("DENO_INSTALL") ?? Path.Combine(home, ".deno"), "bin")) is { } deno) { edit.PrependPath(deno); edit.Report("deno", deno); }
        if (Marked(edit, "bun.lock", "bun.lockb", "bunfig.toml") && Existing(Path.Combine(edit.Get("BUN_INSTALL") ?? Path.Combine(home, ".bun"), "bin")) is { } bun) { edit.PrependPath(bun); edit.Report("bun", bun); }
        if (HasVolta(edit) && Existing(Path.Combine(edit.Get("VOLTA_HOME") ?? Path.Combine(home, ".volta"), "bin")) is { } volta)
        {
            if (edit.Get("VOLTA_HOME") is null) edit.Set("VOLTA_HOME", Path.GetDirectoryName(volta)!);
            edit.PrependPath(volta);
            edit.Report("volta", "Volta shims for the toolchain package.json pins");
        }
        else if (NodeVersion(edit) is { } wanted && InstalledNode(edit, home, wanted) is { } node)
        {
            edit.PrependPath(node);
            edit.Report("node " + wanted.TrimStart('v'), node);
            if (node.Contains(Path.Combine("versions", "node"))) edit.Set("NVM_BIN", node);
        }
        var bin = Path.Combine(edit.Directory, "node_modules", ".bin");
        if (Directory.Exists(bin)) { edit.PrependPath(bin); edit.Report("node_modules", bin); }
        return ValueTask.CompletedTask;
    }

    private static bool Marked(IWorkspaceEnvironmentEdit edit, params string[] files) => files.Any(file => File.Exists(Path.Combine(edit.Directory, file)));

    private static string? Existing(string directory) => Directory.Exists(directory) ? directory : null;

    private static bool HasVolta(IWorkspaceEnvironmentEdit edit)
    {
        var file = Path.Combine(edit.Directory, "package.json");
        if (!File.Exists(file)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("volta", out var volta) && volta.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static string? NodeVersion(IWorkspaceEnvironmentEdit edit)
    {
        foreach (var name in new[] { ".nvmrc", ".node-version" })
        {
            var file = Path.Combine(edit.Directory, name);
            if (!File.Exists(file)) continue;
            var spec = File.ReadLines(file).Select(line => line.Split('#')[0].Trim()).FirstOrDefault(line => line.Length > 0);
            if (spec is { Length: > 1 } && spec[0] == 'v' && char.IsAsciiDigit(spec[1])) spec = spec[1..];
            if (spec is { Length: > 0 }) return spec;
        }
        return null;
    }

    // nvm and fnm both keep one directory per installed version; the highest one the version is a prefix of wins.
    private static string? InstalledNode(IWorkspaceEnvironmentEdit edit, string home, string spec)
    {
        var nvm = edit.Get("NVM_DIR") ?? Path.Combine(home, ".nvm");
        var roots = new List<string> { Path.Combine(nvm, "versions", "node") };
        foreach (var fnm in new[] { edit.Get("FNM_DIR"), Path.Combine(home, ".local", "share", "fnm"), Path.Combine(home, "Library", "Application Support", "fnm") })
            if (fnm is not null) roots.Add(Path.Combine(fnm, "node-versions"));
        var installed = roots.Where(Directory.Exists).SelectMany(root => Directory.EnumerateDirectories(root).Select(directory => (Directory: directory, Version: Parse(Path.GetFileName(directory)))))
            .Where(candidate => candidate.Version is not null).Select(candidate => (candidate.Directory, Version: candidate.Version!)).ToArray();
        if (Concrete(nvm, spec, installed.Select(candidate => candidate.Version).ToArray(), 0) is not { } wanted) return null;
        var best = installed.Where(candidate => Matches(candidate.Version, wanted)).OrderByDescending(candidate => candidate.Version).FirstOrDefault();
        if (best.Directory is null) return null;
        foreach (var bin in new[] { Path.Combine(best.Directory, "bin"), Path.Combine(best.Directory, "installation", "bin"), best.Directory })
            if (File.Exists(Path.Combine(bin, OperatingSystem.IsWindows() ? "node.exe" : "node"))) return bin;
        return null;
    }

    // A number is a version; nvm's `node` and `stable` mean the newest installed, `lts/*` the newest LTS line, and any other
    // name is an alias file under $NVM_DIR/alias holding a version or another alias.
    private static string? Concrete(string nvm, string spec, Version[] installed, int depth)
    {
        if (spec.Length == 0 || depth > 5) return null;
        if (char.IsAsciiDigit(spec[0])) return spec;
        if (spec is "node" or "stable") return installed.Length == 0 ? null : installed.Max()!.ToString(3);
        if (spec == "lts/*")
        {
            var lts = Path.Combine(nvm, "alias", "lts");
            return !Directory.Exists(lts) ? null : Directory.EnumerateFiles(lts).Select(file => Concrete(nvm, "lts/" + Path.GetFileName(file), installed, depth + 1))
                .Select(version => version is null ? null : Parse(version.Split('-')[0])).Where(version => version is not null).Max()?.ToString(3);
        }
        var alias = Path.Combine(nvm, "alias", spec);
        if (spec.Contains("..") || !File.Exists(alias)) return null;
        var target = File.ReadLines(alias).Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
        if (target is { Length: > 1 } && target[0] == 'v' && char.IsAsciiDigit(target[1])) target = target[1..];
        return target is null ? null : Concrete(nvm, target, installed, depth + 1);
    }

    private static Version? Parse(string directory) =>
        Version.TryParse(directory.TrimStart('v'), out var version) ? version : null;

    private static bool Matches(Version installed, string wanted)
    {
        var text = installed.ToString(3);
        return text == wanted || text.StartsWith(wanted + ".", StringComparison.Ordinal);
    }
}