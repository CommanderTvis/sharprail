using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;

namespace SharpRail.Host.Core;

public sealed partial class HostStateStore
{
    private static string SetLanguageServer(string current, string id, string value)
    {
        if (!LanguageServers.Catalog.Any(server => server.Id == id)) throw new ArgumentException("Unknown language server.");
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
            foreach (var server in LanguageServers.Catalog)
            {
                if (!Current.Settings.LspEnabled || !Current.Workspaces.Any(item => item.Path == workspace)) return;
                var options = WorkspaceLanguageConfiguration.Parse(WorkspaceLanguage(workspace, server.Id), server.Id);
                if (options.AutoStart)
                    try { await Languages.ExecuteAsync(new(workspace, Operation: "status", Server: server.Id)); }
                    catch (Exception) { /* The pane reports startup failures through server state. */ }
            }
        });
    }
}