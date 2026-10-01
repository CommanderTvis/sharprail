using System.Text.RegularExpressions;

namespace SharpRail.Plugins.Api;

/// <summary>The API generation a plugin is built against.</summary>
public static class PluginApi
{
    /// <summary>
    /// The shape of the plugin API these assemblies were built against. A plugin declares the generation it
    /// was built against in <see cref="PluginManifest.ApiGeneration"/>; a mismatch is refused before the
    /// plugin loads, naming the plugin, the generation it declares and this one, rather than allowed to
    /// half-load. Incremented with any breaking change to the three API assemblies.
    /// </summary>
    public const int Generation = 1;
}

/// <summary>
/// The names a plugin id is namespaced into. Every wire method, channel, route, layout tool id, preference key
/// and state file a plugin owns is built from its id by one of these helpers, on both sides of a call.
/// </summary>
public static partial class PluginIdentity
{
    /// <summary>
    /// The shape a plugin id must have: lowercase alphanumeric segments joined by hyphens. An external plugin's
    /// id must equal its directory name under a plugin root, so an id cannot claim more than the user placed
    /// there.
    /// </summary>
    public const string IdPattern = "^[a-z0-9]+(?:-[a-z0-9]+)*$";

    /// <summary>Reports whether <paramref name="value"/> is a well-formed plugin id.</summary>
    /// <param name="value">The candidate id.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> matches <see cref="IdPattern"/>.</returns>
    public static bool IsPluginId(string value) => IdRegex().IsMatch(value);

    /// <summary>
    /// Builds the wire name of one of a plugin's request methods, <c>plugin.&lt;id&gt;.&lt;name&gt;</c>. The
    /// generic plugin call carries the id and the name separately; this joined form names the method in
    /// errors and logs.
    /// </summary>
    /// <param name="id">The plugin's id.</param>
    /// <param name="name">The method name declared in the plugin's <see cref="PluginContract"/>.</param>
    /// <returns>The wire method name.</returns>
    public static string MethodName(string id, string name) => $"plugin.{id}.{name}";

    /// <summary>Builds the wire name of one of a plugin's channels, <c>plugin.&lt;id&gt;.&lt;name&gt;</c>.</summary>
    /// <param name="id">The plugin's id.</param>
    /// <param name="name">The channel name declared in the plugin's <see cref="PluginContract"/>.</param>
    /// <returns>The wire channel name.</returns>
    public static string ChannelName(string id, string name) => $"plugin.{id}.{name}";

    /// <summary>
    /// Builds a plugin's HTTP route on the host's loopback server, <c>/plugin/&lt;id&gt;</c> or
    /// <c>/plugin/&lt;id&gt;/&lt;subpath&gt;</c>. This is the path a route registered with the host context's
    /// <c>Route</c> is mounted under.
    /// </summary>
    /// <param name="id">The plugin's id.</param>
    /// <param name="subpath">The path under the plugin's route; empty for the route root.</param>
    /// <returns>The HTTP route.</returns>
    public static string Route(string id, string subpath = "") => subpath.Length == 0 ? $"/plugin/{id}" : $"/plugin/{id}/{subpath}";

    /// <summary>
    /// Builds the id of a plugin-contributed side tool as it appears in a layout, <c>plugin:&lt;id&gt;:&lt;tool&gt;</c>.
    /// The tool name must match one declared in <see cref="PluginContributions.SideTools"/>.
    /// </summary>
    /// <param name="id">The plugin's id.</param>
    /// <param name="tool">The tool name from the manifest.</param>
    /// <returns>The layout tool id.</returns>
    public static string ToolId(string id, string tool) => $"plugin:{id}:{tool}";

    /// <summary>
    /// Parses a layout tool id built by <see cref="ToolId"/> back into its plugin id and tool name. The layout
    /// uses it to route a <c>plugin:</c> tab to its owning plugin, including one whose plugin has since been
    /// removed.
    /// </summary>
    /// <param name="value">The layout tool id to parse.</param>
    /// <returns>The plugin id and tool name, or <see langword="null"/> when <paramref name="value"/> is not a plugin tool id.</returns>
    public static (string PluginId, string Tool)? ParseToolId(string value) =>
        ToolIdRegex().Match(value) is { Success: true } match ? (match.Groups[1].Value, match.Groups[2].Value) : null;

    /// <summary>
    /// Builds a client-local preference key namespaced to a plugin, <c>plugin:&lt;id&gt;:&lt;key&gt;</c>. The
    /// app stores it qualified by the host endpoint; nothing plugin-local crosses the wire.
    /// </summary>
    /// <param name="id">The plugin's id.</param>
    /// <param name="key">The preference name, chosen by the plugin.</param>
    /// <returns>The preference key.</returns>
    public static string PreferenceKey(string id, string key) => $"plugin:{id}:{key}";

    /// <summary>
    /// Builds the path of a plugin's host state file relative to the host state directory,
    /// <c>plugin-state/&lt;id&gt;/&lt;name&gt;.json</c>. Code and state live in sibling trees, so replacing a
    /// plugin does not disturb the state it wrote.
    /// </summary>
    /// <param name="id">The plugin's id.</param>
    /// <param name="name">The state file name, chosen by the plugin.</param>
    /// <returns>The state file's path relative to the host state directory.</returns>
    public static string StateFile(string id, string name) => $"plugin-state/{id}/{name}.json";

    [GeneratedRegex(IdPattern)]
    private static partial Regex IdRegex();

    [GeneratedRegex("^plugin:([a-z0-9-]+):(.+)$")]
    private static partial Regex ToolIdRegex();
}