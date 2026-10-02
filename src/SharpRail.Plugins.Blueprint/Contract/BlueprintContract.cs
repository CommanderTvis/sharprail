using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Blueprint;

public sealed record BlueprintScope(string WorkspaceId);
public sealed record BlueprintOpen(string WorkspaceId, BlueprintSource Source, BlueprintAgentId AgentId);
public sealed record BlueprintOpened(BlueprintState State, string Opening, string SystemPrompt);
public sealed record BlueprintSetAuthor(string WorkspaceId, BlueprintAuthor Author);
public sealed record BlueprintSelect(string WorkspaceId, string ControlId, string OptionId);
public sealed record BlueprintTextEdit(string WorkspaceId, BlueprintEditTarget Target, string Text);
public sealed record BlueprintAck(bool Ok = true);
public sealed record BlueprintSettings;

public static class BlueprintContract
{
    public const string Id = "blueprint";
    public const string File = "BLUEPRINT.md";
    public static readonly PluginMethod<BlueprintOpen, BlueprintOpened> Open = new("open");
    public static readonly PluginMethod<BlueprintSetAuthor, BlueprintAck> SetAuthor = new("setAuthor");
    public static readonly PluginMethod<BlueprintScope, BlueprintAck> Close = new("close");
    public static readonly PluginMethod<BlueprintScope, BlueprintChangedPayload> Get = new("get");
    public static readonly PluginMethod<BlueprintSelect, BlueprintAck> Select = new("select");
    public static readonly PluginMethod<BlueprintTextEdit, BlueprintAck> Edit = new("edit");
    public static readonly PluginMethod<BlueprintScope, BlueprintAck> ConfirmEdits = new("confirmEdits");
    public static readonly PluginMethod<BlueprintScope, BlueprintAck> DiscardEdits = new("discardEdits");
    public static readonly PluginChannel<BlueprintChangedPayload> Changed = PluginChannel<BlueprintChangedPayload>.State("changed", Get, "workspaceId");
    public static readonly PluginContract Contract = PluginContract.Create<BlueprintSettings>(Id, 1,
        [Open, SetAuthor, Close, Get, Select, Edit, ConfirmEdits, DiscardEdits], [Changed]);
    public static readonly PluginManifest Manifest = new(Id, "Blueprint", "pencil-ruler-2", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Authors interactive Blueprint specs and reacts to their edits.",
        EnabledByDefault = true,
        DependsOn = [new("spec-dialect", 1)],
        Host = "SharpRail.Plugins.Blueprint.Host.dll",
        Ui = "SharpRail.Plugins.Blueprint.UI.dll",
        Contributes = new() { FileViewers = [new() { Names = [File], Read = PluginFileRead.None }] }
    };
}