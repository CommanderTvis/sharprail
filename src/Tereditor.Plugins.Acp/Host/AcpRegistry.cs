using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Tereditor.Plugins.Acp.Host;

/// <summary>The registry document: parsing the registry JSON and choosing a distribution for this machine.</summary>
internal static class AcpRegistry
{
    public const string Url = "https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json";

    /// <summary>A minimal list used only when the registry has never been fetched and cannot be now.</summary>
    public const string Seed = """{"version":"1.0.0","agents":[{"id":"pi-acp","name":"pi ACP","version":"0.0.34","description":"ACP adapter for pi coding agent","license":"MIT","distribution":{"npx":{"package":"pi-acp@0.0.34"}}},{"id":"fast-agent","name":"fast-agent","version":"0.10.1","description":"Code and build agents with comprehensive multi-provider support","license":"Apache 2.0","distribution":{"uvx":{"package":"fast-agent-acp==0.10.1","args":["-x"]}}}],"extensions":[]}""";

    /// <summary>This machine's key in a binary distribution, such as <c>darwin-aarch64</c>.</summary>
    public static string Platform()
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
        return os + "-" + architecture;
    }

    public static JsonObject? Parse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject is { } root && root["agents"] is JsonArray ? root : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static IReadOnlyList<string> Kinds(JsonObject? distribution) =>
        [.. new[] { "npx", "uvx", "binary" }.Where(kind => distribution?[kind] is JsonObject)];

    /// <summary>Registry agent ids (exact match) that tereditor supports directly, with the reason shown on the entry. Add a line to extend.</summary>
    internal static readonly IReadOnlyDictionary<string, string> NotRecommendedAgents = new Dictionary<string, string>
    {
        ["claude-acp"] = "Claude Code has its own chat in tereditor. Use that instead.",
        ["pi-acp"] = "Pi has its own chat in tereditor. Use that instead."
    };

    public static AcpRegistryView View(JsonObject root, string source, string? fetchedAt, string? note, string platform)
    {
        var agents = (root["agents"] as JsonArray ?? []).OfType<JsonObject>().Where(agent => AcpFolder.Str(agent, "id").Length > 0).Select(agent =>
        {
            var distribution = agent["distribution"] as JsonObject;
            var kinds = Kinds(distribution);
            return new AcpRegistryAgent(AcpFolder.Str(agent, "id"), AcpFolder.Str(agent, "name"), AcpFolder.Str(agent, "version"), AcpFolder.Str(agent, "description"))
            {
                Repository = Optional(agent, "repository"),
                Website = Optional(agent, "website"),
                Authors = [.. (agent["authors"] as JsonArray ?? []).Select(author => author?.GetValue<string>() ?? "")],
                License = Optional(agent, "license"),
                LicenseUrl = Optional(agent, "license_url"),
                Icon = Optional(agent, "icon"),
                Kinds = kinds,
                Installable = kinds.Any(kind => kind != "binary") || distribution?["binary"]?[platform] is JsonObject,
                NotRecommended = NotRecommendedAgents.GetValueOrDefault(AcpFolder.Str(agent, "id"))
            };
        });
        return new AcpRegistryView([.. agents.OrderBy(agent => agent.NotRecommended is not null).ThenBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)], source) { SchemaVersion = Optional(root, "version"), FetchedAt = fetchedAt, Note = note };
    }

    private static string? Optional(JsonObject node, string name) => AcpFolder.Str(node, name) is { Length: > 0 } text ? text : null;

    /// <summary>
    /// The plan for one agent and kind (the first on offer for this machine when <paramref name="kind"/> is null: npx, then uvx, then binary).
    /// Returns null when the registry does not have the agent or no distribution fits this machine.
    /// </summary>
    public static AcpInstallPlan? Plan(JsonObject root, string agentId, string? kind, string platform)
    {
        var agent = (root["agents"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(candidate => AcpFolder.Str(candidate, "id") == agentId);
        if (agent?["distribution"] is not JsonObject distribution) return null;
        var chosen = kind ?? new[] { "npx", "uvx", "binary" }.FirstOrDefault(candidate => candidate == "binary" ? distribution["binary"]?[platform] is JsonObject : distribution[candidate] is JsonObject);
        var name = AcpFolder.Str(agent, "name");
        var version = AcpFolder.Str(agent, "version");
        var plan = new AcpInstallPlan(agentId, name, version, chosen ?? "") { Repository = Optional(agent, "repository"), License = Optional(agent, "license"), NotRecommended = NotRecommendedAgents.GetValueOrDefault(agentId) };
        switch (chosen)
        {
            case "npx" or "uvx" when distribution[chosen] is JsonObject package:
                {
                    var args = Arguments(package);
                    var spec = AcpFolder.Str(package, "package");
                    var line = chosen == "npx" ? new[] { "bun", "x", spec }.Concat(args) : new[] { "uvx", spec }.Concat(args);
                    return plan with { Source = spec, CommandLine = string.Join(' ', line), Verification = IsPinned(chosen, spec) ? Pinned : NotPinned, Note = $"Runs the package {spec} from the {(chosen == "npx" ? "npm" : "PyPI")} registry on this machine, in the workspace directory." + (chosen == "npx" ? " tereditor runs it with its pinned Bun, which it downloads and checks on first use, so Node and npx are not needed; npx runs it only when Bun cannot be provided." : "") };
                }
            case "binary" when (distribution["binary"]?[platform]) is JsonObject target:
                {
                    var sha = Optional(target, "sha256");
                    return plan with
                    {
                        Source = AcpFolder.Str(target, "archive"),
                        CommandLine = string.Join(' ', new[] { AcpFolder.Str(target, "cmd") }.Concat(Arguments(target))),
                        Sha256 = sha,
                        Verification = sha is null ? NoChecksum : IsSha256(sha) ? Sha256Verified : "The registry's checksum is not a valid SHA-256, so the download will be refused.",
                        Note = sha is null ? "The registry gives no checksum for this download, so it cannot be verified." : "The download is checked against the registry's SHA-256 before it is unpacked."
                    };
                }
            default:
                return null;
        }
    }

    /// <summary>The checksum line for a binary: verified when the registry gives a SHA-256, otherwise honest about having none.</summary>
    public const string Sha256Verified = "SHA-256 verified: the download is checked against the checksum the registry lists before it is unpacked.";
    public const string NoChecksum = "No checksum from the registry: the download cannot be verified.";
    public const string Pinned = "Version pinned: the registry names exactly this package version. The registry gives no checksum or signature for packages, so nothing else can be verified.";
    public const string NotPinned = "Not pinned: the registry does not name an exact package version, so a later version may run. The registry gives no checksum or signature for packages.";

    private static readonly System.Text.RegularExpressions.Regex ExactVersion = new(@"^\d+(\.\d+)*([-+.][0-9A-Za-z.+-]+)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True when a package spec names one exact version: <c>name@1.2.3</c> for npx, <c>name==1.2.3</c> for uvx. A tag such as <c>latest</c> or a range is not pinned.</summary>
    public static bool IsPinned(string kind, string spec)
    {
        if (kind == "uvx") return spec.IndexOf("==", StringComparison.Ordinal) is var at and > 0 && ExactVersion.IsMatch(spec[(at + 2)..]);
        var split = spec.LastIndexOf('@');
        return split > 0 && ExactVersion.IsMatch(spec[(split + 1)..]);
    }

    /// <summary>True for a 64-digit hexadecimal SHA-256.</summary>
    public static bool IsSha256(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);

    public static IReadOnlyList<string> Arguments(JsonObject node) => [.. (node["args"] as JsonArray ?? []).Select(argument => argument?.GetValue<string>() ?? "")];

    public static IReadOnlyDictionary<string, string> Environment(JsonObject node) =>
        (node["env"] as JsonObject ?? []).ToDictionary(pair => pair.Key, pair => pair.Value?.GetValue<string>() ?? "");
}