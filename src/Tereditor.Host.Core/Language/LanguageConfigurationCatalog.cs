using System.Text.Json;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Host.Core.Language;

internal static class LanguageConfigurationCatalog
{
    internal sealed record Description(string Provider, string Version, string Source, string Website, string Repository, string RepositoryLabel, IReadOnlyList<LanguageSettingField> Fields);

    /// <summary>The configuration controls and provenance a known server declares.</summary>
    internal static LanguageResult Read(string id) =>
        Read(LanguageServers.Known(id) ?? throw new ArgumentException("Unknown language server."));

    /// <summary>The configuration controls and provenance the server's provider declares (H21).</summary>
    internal static LanguageResult Read(LanguageServer server)
    {
        var spec = server.Spec;
        var fields = spec.Settings;
        var source = spec.Source.Length > 0 ? spec.Source : server.Package.Length > 0 ? "npm · " + server.Package : "Host installation";
        var (website, repository, repositoryLabel) = (spec.Website, spec.Repository, spec.RepositoryLabel);
        return new("ready", "tereditor pins managed LSP versions so these configuration controls match the server’s configuration structure. Custom executables and servers on PATH may use another version. SDK-bundled servers follow the installed toolchain. Unset fields retain server defaults.", server.Id)
        {
            Configuration = JsonSerializer.Serialize(new Description(server.Implementation, server.Version, source, website, repository, repositoryLabel, fields), HostJson.Web.Info<Description>())
        };
    }
}