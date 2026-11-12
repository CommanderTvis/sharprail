using Tereditor.Plugins.Api.Host;

namespace Tereditor.Host.Core;

/// <summary>The provider the host routes a workspace's branch to, with the plugin id that owns it (H24).</summary>
public sealed record RepoHostRoute(string PluginId, RepoHostProvider Provider);