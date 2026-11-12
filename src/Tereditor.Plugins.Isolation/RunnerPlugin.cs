using System.Text.Json;

namespace Tereditor.Plugins.Isolation;

/// <summary>
/// The runner's <c>plugin</c> mode: one external plugin's process (Security.SPEC.md, one process per plugin). The host
/// starts it with the plugin in <see cref="RunnerInit"/>; the runner loads the UI half before it answers <c>Ready</c>.
/// Each client's UI context is a handle the host lends, and these members act on it.
/// </summary>
public static class RunnerPlugin
{
    public const string Mode = "plugin";

    /// <summary>
    /// Activates the UI half on the context a <see cref="UIActivation"/> names, with its settings mirrored first, and
    /// answers what <c>Activate</c> registered: a list of <see cref="UIRegistration"/>, in order, which the trusted side
    /// replays while its activation guard is open.
    /// </summary>
    public static readonly int ActivateUI = IsoMember.Id("plugin.activate-ui");
    /// <summary>Runs the disposer the activation on the context the payload names returned, and forgets the context.</summary>
    public static readonly int DisposeUI = IsoMember.Id("plugin.dispose-ui");
    /// <summary>
    /// Stores a mirrored member's value for a context (<see cref="UIMirror"/>), and tells the plugin's settings observers
    /// when it is the settings. The trusted side awaits it before it answers the call that changed the value.
    /// </summary>
    public static readonly int Mirror = IsoMember.Id("plugin.mirror");

    public static string Encode(UIActivation activation) => JsonSerializer.Serialize(activation, IsolationJsonContext.Default.UIActivation);
    public static string Encode(UIMirror mirror) => JsonSerializer.Serialize(mirror, IsolationJsonContext.Default.UIMirror);
    public static string Encode(List<UIRegistration> registrations) => JsonSerializer.Serialize(registrations, IsolationJsonContext.Default.ListUIRegistration);

    public static UIActivation DecodeActivation(string json) => Decode(json, IsolationJsonContext.Default.UIActivation);
    public static UIMirror DecodeMirror(string json) => Decode(json, IsolationJsonContext.Default.UIMirror);
    public static List<UIRegistration> DecodeRegistrations(string json) => Decode(json, IsolationJsonContext.Default.ListUIRegistration);

    private static T Decode<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try { return JsonSerializer.Deserialize(json, type) ?? throw new IsoCallException(IsoError.InvalidParams, "Empty arguments."); }
        catch (JsonException error) { throw new IsoCallException(IsoError.InvalidParams, "Invalid arguments: " + error.Message); }
    }
}

/// <summary>Activate the UI half on a client's context, whose settings (the namespace without <c>enabled</c>) come first.</summary>
public sealed record UIActivation(long Context, string Settings);

/// <summary>A mirrored member's value for one context and one argument list (the payload its proxy writes).</summary>
public sealed record UIMirror(long Context, int Member, string Arguments, string Json);

/// <summary>One call the plugin made on its context: a generated member id and its payload.</summary>
public sealed record UIRegistration(int Member, string Payload);