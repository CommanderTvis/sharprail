using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharpRail.Plugins.ClaudeCode.Host;

/// <summary>
/// Reads what <c>claude -p /context --output-format json</c> prints. The JSON wraps the same Markdown report the TUI
/// command renders in its <c>result</c> field, so the report's tables are read by their section titles and header rows.
/// </summary>
internal static class ContextReport
{
    /// <summary>The breakdown inside the CLI's JSON reply, or null when it holds no context report.</summary>
    public static ClaudeContextBreakdown? ParseJson(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.String ? Parse(result.GetString()!) : null;
        }
        catch (JsonException) { return null; }
    }

    public static ClaudeContextBreakdown? Parse(string markdown)
    {
        string? model = null;
        long used = 0, window = 0;
        var sections = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
        List<string[]>? rows = null;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                sections[line.TrimStart('#').Trim()] = rows = [];
                continue;
            }
            if (Field(line, "Model") is { } named) model = named;
            else if (Field(line, "Tokens") is { } tokens && tokens.Split('/') is [var left, var right])
            {
                used = Tokens(left) ?? 0;
                window = Tokens(right.Split('(')[0]) ?? 0;
            }
            else if (rows is not null && line.StartsWith('|'))
            {
                var cells = line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
                // The header row and the |---| rule carry no data.
                if (cells.All(cell => cell.Length > 0 && cell.All(c => c is '-' or ':'))) continue;
                rows.Add(cells);
            }
        }
        if (model is null || window <= 0) return null;
        static IEnumerable<string[]> Data(List<string[]>? table) => table?.Skip(1) ?? [];
        var categories = Data(sections.GetValueOrDefault("Estimated usage by category")).Where(cells => cells.Length >= 2)
            .Select(cells => new ClaudeContextCategory(cells[0], Tokens(cells[1]) ?? 0, cells.Length > 2 ? Percent(cells[2]) : 0)).ToArray();
        ClaudeContextItem[] Items(string title) => [.. Data(sections.GetValueOrDefault(title)).Where(cells => cells.Length >= 3)
            .Select(cells => new ClaudeContextItem(cells[0], cells[1], Tokens(cells[2]) ?? 0))];
        // The memory table reads Type | Path | Tokens; the item is named by its path, sourced by its scope.
        var memory = Data(sections.GetValueOrDefault("Memory Files")).Where(cells => cells.Length >= 3)
            .Select(cells => new ClaudeContextItem(cells[1], cells[0], Tokens(cells[2]) ?? 0)).ToArray();
        return new(model, used, window, categories) { McpTools = Items("MCP Tools"), MemoryFiles = memory, Skills = Items("Skills") };
    }

    /// <summary>
    /// The memory table's paths in the form the configuration resolver lists them: <c>~</c> expanded and absolute. A path
    /// that is a symbolic link, or sits under one, is matched through its target against the resolver's paths.
    /// </summary>
    public static ClaudeContextBreakdown Normalise(ClaudeContextBreakdown breakdown, string cwd, IReadOnlyList<string> known)
    {
        static string Real(string path)
        {
            try { return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return path; }
        }
        var byTarget = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in known) byTarget.TryAdd(Real(path), path);
        ClaudeContextItem Map(ClaudeContextItem file)
        {
            var path = file.Name.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(ClaudeEnvironment.Home(), file.Name[2..]) : file.Name;
            path = Path.GetFullPath(path, cwd);
            return file with { Name = known.Contains(path) ? path : byTarget.GetValueOrDefault(Real(path), path) };
        }
        return breakdown with { MemoryFiles = [.. breakdown.MemoryFiles.Select(Map)] };
    }

    private static string? Field(string line, string name)
    {
        var prefix = $"**{name}:**";
        return line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..].Trim() : null;
    }

    /// <summary>"15.1k", "1m", "~60", "&lt; 20" or "10" as a token count.</summary>
    public static long? Tokens(string text)
    {
        var value = text.Trim().TrimStart('~', '<', ' ').Replace(",", "", StringComparison.Ordinal).Trim();
        if (value.EndsWith(" tokens", StringComparison.Ordinal)) value = value[..^7];
        var scale = 1.0;
        if (value.EndsWith('k') || value.EndsWith('K')) { scale = 1_000; value = value[..^1]; }
        else if (value.EndsWith('m') || value.EndsWith('M')) { scale = 1_000_000; value = value[..^1]; }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? (long)Math.Round(number * scale) : null;
    }

    private static double Percent(string text) =>
        double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

/// <summary>One baseline measurement: where and how to run the CLI, and the files whose change makes the result stale.</summary>
internal sealed record ContextRunSpec(string WorkspaceId, string Binary, string Cwd, IReadOnlyList<string> Flags,
    IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Inputs)
{
    /// <summary>The cache key: a directory and the launch flags that shape what a session there starts with.</summary>
    public string Key => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Binary + "\0" + Cwd + "\0" + string.Join('\0', Flags))))[..16];

    public IReadOnlyList<string> Argv => [Binary, "-p", "/context", "--output-format", "json", "--no-session-persistence", .. Flags];
}

/// <summary>The launch flags of a running session that change what it starts with, read off its process command line.</summary>
internal static class ContextLaunch
{
    private static readonly HashSet<string> ValueFlags = ["--plugin-dir", "--add-dir", "--model", "--effort", "--mcp-config", "--settings", "--setting-sources", "--system-prompt-file"];
    private static readonly HashSet<string> SwitchFlags = ["--strict-mcp-config"];

    /// <summary>
    /// The context-shaping flags of <paramref name="command"/>, in order; session flags (resume, continue, a prompt) are
    /// dropped so the measurement starts fresh and never touches the tab's own conversation.
    /// </summary>
    public static IReadOnlyList<string> Flags(string command, string cwd)
    {
        var flags = new List<string>();
        foreach (var file in SessionContext.AppendedPromptFiles(command, cwd)) flags.AddRange(["--append-system-prompt-file", file]);
        var words = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 1; index < words.Length; index++)
        {
            var word = words[index];
            var equals = word.IndexOf('=', StringComparison.Ordinal);
            var name = equals > 0 ? word[..equals] : word;
            if (SwitchFlags.Contains(name)) flags.Add(name);
            else if (ValueFlags.Contains(name))
            {
                if (equals > 0) flags.AddRange([name, word[(equals + 1)..]]);
                else if (index + 1 < words.Length) flags.AddRange([name, words[++index]]);
            }
        }
        return flags;
    }
}

/// <summary>
/// The host's baseline cache: one entry per (directory, launch flags), measured by running the CLI in the background,
/// at most one run per entry at a time, each with a deadline and cancelled with the activation. An entry goes stale
/// when a file it reads changes; a burst of changes is debounced into one run.
/// </summary>
internal sealed class ContextUsageRunner(Func<ContextRunSpec, TimeSpan, CancellationToken, Task<BoundedRun>> run, TimeSpan timeout, TimeSpan debounce,
    Action<string> finished) : IDisposable
{
    private sealed class Entry(ContextRunSpec spec)
    {
        public ContextRunSpec Spec = spec;
        public ClaudeContextBreakdown? Breakdown;
        public string? Error;
        public string? MeasuredAt;
        public string? Fingerprint;
        public Task? Running;
        public Timer? Pending;
    }

    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = [];
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>Runs started so far, for the checks.</summary>
    public int Runs { get; private set; }

    /// <summary>Size and modification time of every input; a missing file is part of the print, so creating one counts.</summary>
    public static string Fingerprint(IEnumerable<string> paths)
    {
        var builder = new StringBuilder();
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Exists) builder.Append(path).Append('|').Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('\n');
                else if (Directory.Exists(path)) builder.Append(path).Append("|d|").Append(Directory.GetLastWriteTimeUtc(path).Ticks).Append('\n');
                else builder.Append(path).Append("|-\n");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { builder.Append(path).Append("|?\n"); }
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }

    /// <summary>The cached answer for this spec; starts a background run when there is none, it is stale, or a refresh is asked for.</summary>
    public ClaudeContextUsage Get(ContextRunSpec spec, bool refresh)
    {
        var fingerprint = Fingerprint(spec.Inputs);
        lock (gate)
        {
            if (!entries.TryGetValue(spec.Key, out var entry)) entries[spec.Key] = entry = new(spec);
            entry.Spec = spec;
            if (entry.Running is null && (refresh || entry.Fingerprint != fingerprint)) Start(entry, fingerprint);
            return Snapshot(entry);
        }
    }

    private static ClaudeContextUsage Snapshot(Entry entry) =>
        new(entry.Running is not null) { Breakdown = entry.Breakdown, Error = entry.Error, MeasuredAt = entry.MeasuredAt };

    // Called under the gate.
    private void Start(Entry entry, string fingerprint)
    {
        if (lifetime.IsCancellationRequested) return;
        Runs++;
        var spec = entry.Spec;
        entry.Running = Task.Run(async () =>
        {
            ClaudeContextBreakdown? breakdown = null;
            string? error = null;
            try
            {
                var result = await run(spec, timeout, lifetime.Token);
                if (result.LaunchFailed) error = $"Could not run {spec.Binary}: {result.Err.Trim()}";
                else if (result.TimedOut) error = $"claude -p /context did not finish within {timeout.TotalSeconds:0} s.";
                else if ((breakdown = ContextReport.ParseJson(result.Out)) is null)
                    error = result.Err.Trim() is { Length: > 0 } stderr ? stderr : "Claude Code printed no context report.";
                else breakdown = ContextReport.Normalise(breakdown, spec.Cwd, spec.Inputs);
            }
            catch (OperationCanceledException)
            {
                lock (gate) entry.Running = null;
                return;
            }
            catch (Exception failure) { error = failure.Message; }
            lock (gate)
            {
                entry.Running = null;
                entry.Fingerprint = fingerprint;
                entry.Error = error;
                if (breakdown is not null)
                {
                    entry.Breakdown = breakdown;
                    entry.MeasuredAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                }
            }
            finished(spec.WorkspaceId);
        });
    }

    /// <summary>
    /// Files changed: every entry reading one of them measures again once the burst has been quiet for the debounce,
    /// and only if its inputs really differ from what it last measured.
    /// </summary>
    public void Changed(IReadOnlyCollection<string> paths, string? workspaceId = null, bool all = false)
    {
        lock (gate)
            foreach (var entry in entries.Values)
            {
                if (entry.Breakdown is null && entry.Error is null && entry.Running is null) continue;
                var reads = all ? entry.Spec.WorkspaceId == workspaceId : paths.Any(path => entry.Spec.Inputs.Any(input =>
                    path == input || path.StartsWith(input.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
                if (!reads) continue;
                var target = entry;
                target.Pending?.Dispose();
                target.Pending = new Timer(_ =>
                {
                    var fingerprint = Fingerprint(target.Spec.Inputs);
                    lock (gate)
                    {
                        target.Pending?.Dispose();
                        target.Pending = null;
                        if (target.Running is null && target.Fingerprint != fingerprint) Start(target, fingerprint);
                    }
                }, null, debounce, Timeout.InfiniteTimeSpan);
            }
    }

    /// <summary>Waits for every run under way, for the checks.</summary>
    public async Task IdleAsync()
    {
        while (true)
        {
            Task[] running;
            lock (gate) running = [.. entries.Values.Select(entry => entry.Running).OfType<Task>()];
            if (running.Length == 0) return;
            await Task.WhenAll(running);
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lock (gate)
            foreach (var entry in entries.Values) { entry.Pending?.Dispose(); entry.Pending = null; }
    }
}