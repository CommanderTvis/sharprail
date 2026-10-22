using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;

namespace SharpRail.Host.Core;

public sealed partial class HostStateStore
{
    private static string SetLanguageServer(string current, string id, string value)
    {
        if (LanguageServers.Known(id) is null) throw new ArgumentException("Unknown language server.");
        if (value.Length > 16 * 1024) throw new ArgumentException("Language server settings are too large.");
        var options = JsonSerializer.Deserialize<LanguageServerOptions>(value, JsonSerializerOptions.Web)
            ?? throw new ArgumentException("Language server settings are required.");
        if (options.Command is null || options.Command.Contains('\0') || options.Arguments?.Any(arg => arg is null || arg.Contains('\0')) == true)
            throw new ArgumentException("Launcher command and arguments cannot contain null values or NUL characters.");
        if (options.Arguments is not null && string.IsNullOrWhiteSpace(options.Command))
            throw new ArgumentException("Set an executable override before customizing its arguments.");
        var settings = string.IsNullOrWhiteSpace(current) ? new Dictionary<string, LanguageServerOptions>() :
            JsonSerializer.Deserialize<Dictionary<string, LanguageServerOptions>>(current, JsonSerializerOptions.Web)!;
        settings[id] = options;
        return JsonSerializer.Serialize(settings, JsonSerializerOptions.Web);
    }

    /// <summary>Sets or, with an empty value, clears the host-wide path of a tool language servers need; the path must be absolute.</summary>
    private static string SetLanguageTool(string current, string executable, string value)
    {
        if (executable.Length is 0 or > 64 || executable.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
            throw new ArgumentException("Invalid tool name.");
        var path = value.Trim();
        if (path.Length > 4096 || path.Contains('\0') || path.Length > 0 && !Path.IsPathFullyQualified(path)) throw new ArgumentException("Choose an absolute path to the executable.");
        var tools = LanguageServers.ParseTools(current);
        if (path.Length == 0) tools.Remove(executable); else tools[executable] = path;
        return tools.Count == 0 ? "" : JsonSerializer.Serialize(new SortedDictionary<string, string>(tools, StringComparer.Ordinal));
    }
}
public sealed partial class HostStateStore
{
    internal string WorkspaceLanguage(string workspace, string server)
    {
        var local = LocalLanguage(workspace, server);
        var project = ProjectOfWorkspace(workspace);
        return project is not null && ProjectLanguage(project).Servers.TryGetValue(server, out var portable)
            ? ProjectLanguageFile.Merge(local, portable) : local;
    }

    private string LocalLanguage(string workspace, string server)
    {
        lock (gate)
        {
            var settings = state.WorkspaceLanguages.GetValueOrDefault(workspace) ?? "{}";
            var file = LanguageMetadataPath(workspace, server);
            if (file is not null && File.Exists(file))
            {
                var entry = JsonSerializer.Deserialize<LanguageMetadata>(File.ReadAllText(file))
                    ?? throw new JsonException("Invalid workspace language metadata.");
                var values = System.Text.Json.Nodes.JsonNode.Parse(settings)!.AsObject();
                if (entry.Workspace != workspace || entry.Server != server) throw new JsonException("Invalid workspace language metadata identity.");
                if (values[server]?.ToJsonString() != entry.Configuration)
                {
                    values[server] = System.Text.Json.Nodes.JsonNode.Parse(entry.Configuration);
                    settings = values.ToJsonString();
                    var configurations = state.WorkspaceLanguages.ToDictionary();
                    configurations[workspace] = settings;
                    Publish(state with { WorkspaceLanguages = configurations }, persist: false);
                }
            }
            return System.Text.Json.Nodes.JsonNode.Parse(settings)?[server]?.ToJsonString() ?? "{}";
        }
    }

    internal void SaveWorkspaceLanguage(string workspace, string server, string configuration)
    {
        var validated = WorkspaceLanguageConfiguration.Serialize(WorkspaceLanguageConfiguration.Parse(configuration, server));
        var project = ProjectOfWorkspace(workspace);
        if (project is not null && ProjectLanguage(project).Servers.TryGetValue(server, out var portable))
            validated = ProjectLanguageFile.Localize(LocalLanguage(workspace, server), validated, portable);
        lock (gate)
        {
            var configurations = state.WorkspaceLanguages.ToDictionary();
            var servers = System.Text.Json.Nodes.JsonNode.Parse(configurations.GetValueOrDefault(workspace) ?? "{}")!.AsObject();
            if (validated == "{}" && project is not null && ProjectLanguage(project).Servers.ContainsKey(server)) servers.Remove(server);
            else servers[server] = System.Text.Json.Nodes.JsonNode.Parse(validated);
            configurations[workspace] = servers.ToJsonString();
            Publish(state with { WorkspaceLanguages = configurations }, persist: true);
        }
    }

    internal void StartWorkspaceLanguages(string workspace)
    {
        _ = Task.Run(async () =>
        {
            foreach (var server in LanguageCatalog)
            {
                if (!Current.Settings.LspEnabled || !Current.Workspaces.Any(item => item.Path == workspace) && !Current.Projects.Contains(workspace)) return;
                var options = WorkspaceLanguageConfiguration.Parse(WorkspaceLanguage(workspace, server.Id), server.Id);
                if (options.AutoStart)
                    try { await Languages.ExecuteAsync(new(workspace, Operation: "status", Server: server.Id, Origin: LanguageOrigins.Config)); }
                    catch (Exception) { /* The pane reports startup failures through server state. */ }
            }
            // The project's shared choices (.sharprail/language.toml autoStart, or its local override) start in every workspace of the project.
            try { await ProjectLanguageAutostartAsync(new(workspace, Operation: "prepareAgent"), CancellationToken.None); }
            catch (Exception) { /* A workspace outside a registered project has no shared choices. */ }
        });
    }

    /// <summary>
    /// Starts language servers for a new workspace without waiting for them: "auto" means every server whose files are present, otherwise server ids or
    /// language names. Returns each requested server with its state right after the start was requested (starting, ready, needs-acceptance or failed).
    /// </summary>
    internal async Task<IReadOnlyList<(string Server, string State, string Detail)>> StartRequestedLanguagesAsync(string workspace, IReadOnlyList<string> requested, CancellationToken token)
    {
        var ids = new List<string>();
        var rows = new List<(string Server, string State, string Detail)>();
        foreach (var name in requested.Select(item => item.Trim()).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (name.Equals("auto", StringComparison.OrdinalIgnoreCase)) ids.AddRange(await Task.Run(() => Languages.DetectedServers(workspace), token));
            else if (LanguageCatalog.FirstOrDefault(server => server.Id.Equals(name, StringComparison.OrdinalIgnoreCase) || server.Spec.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase)) is { } known) ids.Add(known.Id);
            else rows.Add((name, "failed", $"Unknown language server or language '{name}'. Use \"auto\" or one of: {string.Join(", ", LanguageCatalog.Select(server => server.Id))}."));
        }
        var started = await Task.WhenAll(ids.Distinct().Select(id => Task.Run(async () =>
        {
            try
            {
                var result = await Languages.ExecuteAsync(new(workspace, Operation: "status", Server: id, Origin: LanguageOrigins.Workspace), token);
                var state = result.State switch
                {
                    "installing" or "initializing" or "actionRequired" => "starting",
                    "ready" => "ready",
                    "requiresAcceptance" => "needs-acceptance",
                    _ => "failed"
                };
                return (id, state, result.Detail);
            }
            catch (Exception error) when (error is not OperationCanceledException) { return (id, "failed", error.Message); }
        }, token)));
        rows.AddRange(started);
        return rows;
    }
}