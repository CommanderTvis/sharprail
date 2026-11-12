using System.Text.Json.Serialization;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Chat;

namespace SharpRail.Plugins.Pi;

public sealed record PiNoParams;

/// <summary>Whether <c>pi</c> was found and which version it reports.</summary>
public sealed record PiStatus(bool Installed, string Command)
{
    public string? Version { get; init; }
    public string? Detail { get; init; }
}

public sealed record PiSettings
{
    /// <summary>The executable; the system installation on PATH by default.</summary>
    public string Command { get; set; } = "pi";
    /// <summary><c>--model</c>: a pattern or <c>provider/id</c>; blank uses Pi's default.</summary>
    public string Model { get; set; } = "";
    /// <summary>The thinking level a new chat starts with; blank uses Pi's default.</summary>
    public string Thinking { get; set; } = "";
}

public static class PiThinking
{
    /// <summary>The levels <c>set_thinking_level</c> accepts (Pi's RPC docs).</summary>
    public static readonly IReadOnlyList<string> Levels = ["off", "minimal", "low", "medium", "high", "xhigh"];
}

public static class PiContract
{
    public const int WireVersion = 1;

    public const string DocumentScheme = "pi-chat";

    public static readonly PluginMethod<PiNoParams, PiStatus> Status = new("status");

    /// <summary>The chat contract is shared with the other chat plugins: Pi serves its methods and its updates channel.</summary>
    public static readonly PluginContract Contract = PluginContract.Create<PiSettings>(PiManifest.Id, WireVersion,
        [.. ChatContract.Methods, Status], [ChatContract.Updates], PiJson.Default, ChatContract.Json);
}

[JsonSerializable(typeof(PiSettings))]
[JsonSerializable(typeof(PiNoParams))]
[JsonSerializable(typeof(PiStatus))]
internal sealed partial class PiJson : JsonSerializerContext;