namespace Tereditor.Plugins.Acp.Host;

/// <summary>How a registry npx agent is started with Bun instead of npx: the same pinned package, the runner changes.</summary>
internal static class AcpRunner
{
    /// <summary>
    /// <c>npx -y name@1.2.3 args</c> becomes <c>bun x name@1.2.3 args</c> (Bun's <c>bunx</c>, which has no prompt to answer and so no <c>-y</c>).
    /// A leading <c>-y</c> or <c>--yes</c> is dropped; every other argument, the package spec included, is kept as it was.
    /// </summary>
    internal static (string Command, IReadOnlyList<string> Args) Bun(string bun, IReadOnlyList<string> npxArgs) =>
        (bun, ["x", .. npxArgs.SkipWhile(argument => argument is "-y" or "--yes")]);

    /// <summary>
    /// The agent's environment plus the guard for Bun: it reads <c>bunfig.toml</c> (which can preload scripts) and <c>.env</c> from the working directory,
    /// which is the workspace, and so does every Bun the agent starts. <c>BUN_OPTIONS</c> points them all at an empty config and turns .env off,
    /// and Bun's home (its package cache) stays under <paramref name="home"/>. The agent's own entries win.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> GuardedEnvironment(string home, IReadOnlyDictionary<string, string> env)
    {
        var config = Path.Combine(home, "empty-bunfig.toml");
        Directory.CreateDirectory(home);
        if (!File.Exists(config)) File.WriteAllText(config, "");
        var guarded = new Dictionary<string, string> { ["BUN_INSTALL"] = home, ["BUN_OPTIONS"] = $"\"--config={config}\" --no-env-file", ["DO_NOT_TRACK"] = "1" };
        foreach (var (name, value) in env) guarded[name] = value;
        return guarded;
    }
}