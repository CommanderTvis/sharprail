using System.Collections;
using System.Diagnostics;
using System.Text;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Plugins;

/// <summary>A workspace's resolved environment: the variables its shells get beyond the host's own, and what the steps reported.</summary>
internal sealed record ResolvedEnvironment(IReadOnlyDictionary<string, string> Variables, IReadOnlyList<WorkspaceEnvironmentFact> Facts);

/// <summary>One plugin's registered workspace environment step.</summary>
internal sealed record EnvironmentProvider(string PluginId, int Order, Func<IWorkspaceEnvironmentEdit, CancellationToken, ValueTask> Run);

/// <summary>The environment a workspace's shell would start with, edited in order by the enabled plugins' steps.</summary>
internal sealed class WorkspaceEnvironmentEdit : IWorkspaceEnvironmentEdit
{
    private static StringComparer Names => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Dictionary<string, string> variables;
    private readonly HashSet<string> edited = new(Names);
    private readonly Dictionary<string, SortedSet<string>> touched = [];
    private readonly Dictionary<string, List<string>> shellInit = [];
    private readonly List<(string Plugin, string Label, string? Detail)> reports = [];

    public WorkspaceEnvironmentEdit(string directory, string? defaultDirectory, IReadOnlyDictionary<string, string> host, IPluginLogger log)
    {
        Directory = directory;
        DefaultDirectory = defaultDirectory is not null && !SamePath(defaultDirectory, directory) ? defaultDirectory : null;
        Log = log;
        variables = new Dictionary<string, string>(host, Names);
        // The host's session token authorizes the host process; no tool or .envrc of the workspace receives it.
        variables.Remove("SHARPRAIL_TOKEN");
    }

    public string Directory { get; }
    public string? DefaultDirectory { get; }
    public IPluginLogger Log { get; set; }

    /// <summary>The plugin whose step is running; what it sets and reports is attributed to it.</summary>
    public string Plugin { get; set; } = "";

    public string? Get(string name) => variables.GetValueOrDefault(name);

    public bool Edited(string name) => edited.Contains(name);

    public void Set(string name, string? value)
    {
        if (string.Equals(name, "SHARPRAIL_TOKEN", StringComparison.OrdinalIgnoreCase)) return;
        edited.Add(name);
        Touch(name);
        if (value is null) variables.Remove(name); else variables[name] = value;
    }

    public void PrependPath(string directory)
    {
        var entries = (variables.GetValueOrDefault("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length > 0 && string.Equals(entries[0], directory, Comparison)) return;
        variables["PATH"] = string.Join(Path.PathSeparator, entries.Where(existing => !string.Equals(existing, directory, Comparison)).Prepend(directory));
        Touch("PATH");
    }

    private void Touch(string name)
    {
        if (!touched.TryGetValue(Plugin, out var names)) touched[Plugin] = names = new SortedSet<string>(StringComparer.Ordinal);
        names.Add(name);
    }

    public void ShellInit(string shell, string script)
    {
        shell = shell.ToLowerInvariant();
        if (shell is not ("zsh" or "fish") || script.Length == 0) return;
        if (!shellInit.TryGetValue(shell, out var scripts)) shellInit[shell] = scripts = [];
        scripts.Add(script);
    }

    public void Report(string label, string? detail = null) => reports.Add((Plugin, label, detail));

    public string? Find(string program)
    {
        string[] extensions = OperatingSystem.IsWindows() ? ["", ".exe", ".cmd", ".bat"] : [""];
        foreach (var entry in (variables.GetValueOrDefault("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(entry, program + extension);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    public async ValueTask<EnvironmentToolResult> RunAsync(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = directory ?? Directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var (name, value) in variables) start.Environment[name] = value;
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new(null, "", error.Message);
        }
        process.StandardInput.Close();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ToolDeadline);
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error2 = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return new(process.ExitCode, await output, await error2);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception kill) when (kill is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            cancellationToken.ThrowIfCancellationRequested();
            return new(null, "", $"{Path.GetFileName(program)} did not finish within {ToolDeadline.TotalSeconds:0} seconds.");
        }
    }

    public bool MatchesDefault(IReadOnlyList<string> files)
    {
        if (DefaultDirectory is not { } origin || !files.Any(file => File.Exists(Path.Combine(Directory, file)))) return false;
        foreach (var file in files)
        {
            var mine = Path.Combine(Directory, file);
            var theirs = Path.Combine(origin, file);
            if (File.Exists(mine) != File.Exists(theirs)) return false;
            if (File.Exists(mine) && !File.ReadAllBytes(mine).AsSpan().SequenceEqual(File.ReadAllBytes(theirs))) return false;
        }
        return true;
    }

    /// <summary>
    /// The variables that differ from the host's own, the shell scripts when a shim directory is given, and the reported
    /// facts with the variables each step set. A removal cannot reach a shell that inherits the host's value.
    /// </summary>
    public ResolvedEnvironment Result(IReadOnlyDictionary<string, string> host, Func<string>? integration)
    {
        var changed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in variables)
            if (!host.TryGetValue(name, out var original) || original != value) changed[name] = value;
        if (shellInit.Count > 0 && integration is not null)
        {
            foreach (var (shell, scripts) in shellInit) changed[ShellIntegration.InitPrefix + shell.ToUpperInvariant()] = string.Join('\n', scripts);
            changed[ShellIntegration.DirectoryVariable] = integration();
        }
        var facts = reports.Select(report =>
        {
            var sets = touched.TryGetValue(report.Plugin, out var names) && names.Count > 0 ? "Sets " + string.Join(", ", names) : null;
            var detail = string.Join('\n', new[] { report.Detail, sets }.Where(part => !string.IsNullOrEmpty(part)));
            return new WorkspaceEnvironmentFact(report.Plugin, report.Label, detail.Length == 0 ? null : detail);
        }).ToArray();
        return new(changed, facts);
    }

    internal static TimeSpan ToolDeadline { get; set; } = TimeSpan.FromSeconds(30);

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), Comparison);
}

/// <summary>
/// Resolves each workspace's environment off the caller's thread, shares a resolution already running, and keeps the
/// last good result so a shell that cannot wait for a slow tool still starts with the previous environment.
/// </summary>
internal sealed class EnvironmentResolutions(Func<string, CancellationToken, Task<ResolvedEnvironment>> resolve,
    Action<string> warn, CancellationToken lifetime, Action<string, ResolvedEnvironment>? completed = null)
{
    private sealed class Entry
    {
        public Task<ResolvedEnvironment>? Running;
        public ResolvedEnvironment? Last;
    }

    private static readonly ResolvedEnvironment None = new(new Dictionary<string, string>(), []);
    private readonly Dictionary<string, Entry> entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Lock gate = new();

    /// <summary>How long a starting shell waits for the tools before it uses the previous result.</summary>
    public static TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Starts a resolution unless one is running.</summary>
    public Task<ResolvedEnvironment> Start(string directory)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(directory, out var entry)) entries[directory] = entry = new Entry();
            if (entry.Running is { IsCompleted: false } running) return running;
            return entry.Running = Task.Run(async () =>
            {
                try
                {
                    var result = await resolve(directory, lifetime);
                    lock (gate) entry.Last = result;
                    completed?.Invoke(directory, result);
                    return result;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    warn($"The environment of {directory} could not be resolved: {error.Message}");
                    return Resolved(directory);
                }
            });
        }
    }

    /// <summary>Waits for a fresh resolution for at most the budget; a slower one keeps running and serves the next shell.</summary>
    public async ValueTask PrepareAsync(string directory, CancellationToken cancellationToken)
    {
        var running = Start(directory);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        var finished = await Task.WhenAny(running, Task.Delay(Budget, wait.Token));
        await wait.CancelAsync();
        if (finished == running) return;
        cancellationToken.ThrowIfCancellationRequested();
        warn($"The environment of {directory} is still resolving after {Budget.TotalSeconds:0} seconds; this shell starts with the previous one.");
    }

    public IReadOnlyDictionary<string, string> Contribution(string directory) => Resolved(directory).Variables;

    public ResolvedEnvironment Resolved(string directory)
    {
        lock (gate) return entries.TryGetValue(directory, out var entry) ? entry.Last ?? None : None;
    }
}

public sealed partial class PluginRuntime
{
    private EnvironmentResolutions? resolutions;

    private EnvironmentResolutions Resolutions => resolutions ??= new(ResolveEnvironmentAsync, message => Log("host", "warn", message, null), lifetime.Token, NotifyEnvironment);

    private IReadOnlyList<(string Plugin, EnvironmentProvider Provider)> EnvironmentProviders() =>
        Active().SelectMany(active => active.Tables.EnvironmentProviders.Select(provider => (active.Entry.Id, provider)))
            .OrderBy(pair => pair.provider.Order).ThenBy(pair => pair.Item1, StringComparer.Ordinal).ToArray();

    private async Task<ResolvedEnvironment> ResolveEnvironmentAsync(string directory, CancellationToken cancellationToken)
    {
        if (EnvironmentProviders().Count == 0) return new(new Dictionary<string, string>(), []);
        var workspace = (await WorkspacesAsync(null, cancellationToken)).FirstOrDefault(candidate => candidate.Path == directory || candidate.Id == directory);
        var origin = workspace is null || workspace.IsDefault ? null : Projects().FirstOrDefault(project => project.Id == workspace.ProjectId)?.Path;
        return await ResolveEnvironmentAsync(directory, origin, cancellationToken);
    }

    /// <summary>Runs the enabled steps for a directory against the process environment; <paramref name="origin"/> is its project's default workspace.</summary>
    internal async Task<ResolvedEnvironment> ResolveEnvironmentAsync(string directory, string? origin, CancellationToken cancellationToken)
    {
        var host = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables()) host[(string)entry.Key] = (string?)entry.Value ?? "";
        var edit = new WorkspaceEnvironmentEdit(directory, origin, host, new HostLog(this, ""));
        foreach (var (plugin, provider) in EnvironmentProviders())
        {
            cancellationToken.ThrowIfCancellationRequested();
            edit.Log = new HostLog(this, plugin);
            edit.Plugin = plugin;
            try { await provider.Run(edit, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log(plugin, "warn", $"its environment step was skipped for {directory}: {error.Message}", null);
            }
        }
        return edit.Result(host, () => ShellIntegration.Ensure(Path.Combine(Seams.StateDirectory ?? Path.GetTempPath(), "shell-integration")));
    }

    private void NotifyEnvironment(string directory, ResolvedEnvironment resolved)
    {
        foreach (var (entry, tables) in Active())
            foreach (var observer in tables.EnvironmentObservers) Guard(entry.Id, () => observer(directory, resolved.Facts));
    }

    private sealed class HostLog(PluginRuntime runtime, string scope) : IPluginLogger
    {
        public void Debug(string message, object? fields = null) => runtime.Log(scope, "debug", message, fields);
        public void Info(string message, object? fields = null) => runtime.Log(scope, "info", message, fields);
        public void Warn(string message, object? fields = null) => runtime.Log(scope, "warn", message, fields);
        public void Error(string message, object? fields = null) => runtime.Log(scope, "error", message, fields);
    }

    private async ValueTask PrepareEnvironment(TerminalRef terminal, CancellationToken cancellationToken)
    {
        if (EnvironmentProviders().Count > 0) await Resolutions.PrepareAsync(terminal.WorkspaceId, cancellationToken);
    }

    /// <summary>Resolves a new workspace's environment in the background, so trust is settled before its first terminal.</summary>
    private void WarmEnvironment(string directory)
    {
        if (EnvironmentProviders().Count > 0) _ = Resolutions.Start(directory);
    }
}
