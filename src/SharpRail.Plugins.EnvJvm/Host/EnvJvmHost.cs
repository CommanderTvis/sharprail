using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.EnvJvm.Host;

/// <summary>
/// Points <c>JAVA_HOME</c> and PATH at the JDK a workspace chooses, so Gradle, Maven, language servers and agents in a
/// worktree build with the JDK the project pins and not whichever one the host happens to have. A <c>.sdkmanrc</c>
/// (<c>java=21.0.2-tem</c>) names an sdkman candidate; a <c>.java-version</c> names a jenv version, or failing that an
/// sdkman candidate by version prefix. A choice an earlier step made (direnv, mise) is kept. Nothing is installed.
/// </summary>
public sealed class EnvJvmHost : PluginHostModule
{
    public override PluginContract Contract => EnvJvmContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        context.WorkspaceEnvironment(EnvJvmContract.Order, Apply);
        return ValueTask.FromResult<PluginDisposer?>(null);
    }

    internal static ValueTask Apply(IWorkspaceEnvironmentEdit edit, CancellationToken cancellationToken)
    {
        if (edit.Edited("JAVA_HOME")) return ValueTask.CompletedTask;
        var home = edit.Get("HOME") ?? edit.Get("USERPROFILE") ?? "";
        var sdkman = Sdkman(edit, home);
        string? chosen = null;
        if (Value(Path.Combine(edit.Directory, ".sdkmanrc"), "java") is { } candidate)
        {
            chosen = Valid(Path.Combine(sdkman, candidate));
            if (chosen is null) edit.Log.Warn($".sdkmanrc asks for java {candidate}, which sdkman has not installed. Run `sdk env install` in {edit.Directory}.");
        }
        else if (FirstToken(Path.Combine(edit.Directory, ".java-version")) is { } version)
        {
            chosen = Valid(Resolve(Path.Combine(edit.Get("JENV_ROOT") ?? Path.Combine(home, ".jenv"), "versions", version))) ?? Newest(sdkman, version);
            if (chosen is null) edit.Log.Warn($".java-version asks for java {version}, which neither jenv nor sdkman has installed.");
        }
        if (chosen is null) return ValueTask.CompletedTask;
        edit.Set("JAVA_HOME", chosen);
        edit.PrependPath(Path.Combine(chosen, "bin"));
        edit.Report("JDK " + Path.GetFileName(chosen.TrimEnd(Path.DirectorySeparatorChar, '/')), chosen);
        return ValueTask.CompletedTask;
    }

    /// <summary>Homebrew prefixes, whose <c>opt/sdkman-cli/libexec</c> is where Homebrew's sdkman keeps its candidates.</summary>
    internal static string[] BrewPrefixes { get; set; } = ["/opt/homebrew", "/usr/local", "/home/linuxbrew/.linuxbrew"];

    // $SDKMAN_DIR, then ~/.sdkman, then Homebrew's sdkman; an app started from the dock has no SDKMAN_DIR.
    private static string Sdkman(IWorkspaceEnvironmentEdit edit, string home)
    {
        var roots = new List<string>();
        if (edit.Get("SDKMAN_DIR") is { Length: > 0 } configured) roots.Add(configured);
        if (home.Length > 0) roots.Add(Path.Combine(home, ".sdkman"));
        roots.AddRange(BrewPrefixes.Select(prefix => Path.Combine(prefix, "opt", "sdkman-cli", "libexec")));
        var candidates = roots.Select(root => Path.Combine(root, "candidates", "java")).ToArray();
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    // jenv's version entries are links to the JDK's home.
    private static string Resolve(string path)
    {
        try { return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path; }
        catch (IOException) { return path; }
    }

    private static string? Valid(string home) =>
        File.Exists(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java")) ? home : null;

    // "21" selects the highest installed 21.x.y-vendor candidate.
    private static string? Newest(string sdkman, string version) =>
        !Directory.Exists(sdkman) ? null : Directory.EnumerateDirectories(sdkman)
            .Where(directory => Path.GetFileName(directory) is var name && (name == version || name.StartsWith(version + ".", StringComparison.Ordinal) || name.StartsWith(version + "-", StringComparison.Ordinal)))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Select(Valid).FirstOrDefault(home => home is not null);

    private static string? Value(string file, string key)
    {
        if (!File.Exists(file)) return null;
        foreach (var line in File.ReadLines(file))
        {
            var text = line.Split('#')[0].Trim();
            var at = text.IndexOf('=');
            if (at > 0 && text[..at].Trim() == key && text[(at + 1)..].Trim() is { Length: > 0 } value) return value;
        }
        return null;
    }

    private static string? FirstToken(string file) =>
        File.Exists(file) ? File.ReadLines(file).Select(line => line.Split('#')[0].Trim()).FirstOrDefault(line => line.Length > 0) : null;
}
