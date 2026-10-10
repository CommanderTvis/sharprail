using System.Text.Json.Serialization;

namespace SharpRail.Plugins.Blueprint;

public sealed record BlueprintOption(string Id, string Label, string Axis);
public enum BlueprintControlKind { Select, Multi }
public sealed record BlueprintControl(string Id, BlueprintControlKind Kind, string Title,
    IReadOnlyList<BlueprintOption> Options, IReadOnlyList<string> SelectedIds, bool Pending, bool Locked);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BlueprintProse), "prose")]
[JsonDerivedType(typeof(BlueprintControlBlock), "control")]
public abstract record BlueprintBlock(string Id);
public sealed record BlueprintProse(string Id, string Text) : BlueprintBlock(Id);
public sealed record BlueprintControlBlock(string Id, BlueprintControl Control) : BlueprintBlock(Id);
public sealed record BlueprintBlockLines(int StartLine, int EndLine);
public sealed record BlueprintDoc(IReadOnlyList<BlueprintBlock> Blocks, string Frontmatter);
public enum BlueprintAgentId { Pi, Claude, Codex }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BlueprintIdea), "idea")]
[JsonDerivedType(typeof(BlueprintProduct), "product")]
[JsonDerivedType(typeof(BlueprintSpec), "spec")]
public abstract record BlueprintSource;
public sealed record BlueprintIdea(string Brief) : BlueprintSource;
public sealed record BlueprintProduct : BlueprintSource;
public sealed record BlueprintSpec(string Path) : BlueprintSource;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BlueprintFrontmatterTarget), "frontmatter")]
[JsonDerivedType(typeof(BlueprintProseTarget), "prose")]
[JsonDerivedType(typeof(BlueprintOptionLabelTarget), "option-label")]
[JsonDerivedType(typeof(BlueprintOptionAxisTarget), "option-axis")]
public abstract record BlueprintEditTarget;
public sealed record BlueprintFrontmatterTarget : BlueprintEditTarget;
public sealed record BlueprintProseTarget(string BlockId) : BlueprintEditTarget;
public abstract record BlueprintOptionTarget(string ControlId, string OptionId) : BlueprintEditTarget;
public sealed record BlueprintOptionLabelTarget(string ControlId, string OptionId) : BlueprintOptionTarget(ControlId, OptionId);
public sealed record BlueprintOptionAxisTarget(string ControlId, string OptionId) : BlueprintOptionTarget(ControlId, OptionId);
public sealed record BlueprintEdit(BlueprintEditTarget Target, string Before, string After);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BlueprintControlAdded), "control-added")]
[JsonDerivedType(typeof(BlueprintControlRemoved), "control-removed")]
[JsonDerivedType(typeof(BlueprintControlReselected), "control-reselected")]
[JsonDerivedType(typeof(BlueprintControlOptionsChanged), "control-options-changed")]
[JsonDerivedType(typeof(BlueprintProseChanged), "prose-changed")]
public abstract record BlueprintChange;
public sealed record BlueprintControlAdded(string ControlId, string Title) : BlueprintChange;
public sealed record BlueprintControlRemoved(string ControlId, string Title) : BlueprintChange;
public sealed record BlueprintControlReselected(string ControlId, string Title, string From, string To) : BlueprintChange;
public sealed record BlueprintControlOptionsChanged(string ControlId, string Title) : BlueprintChange;
public sealed record BlueprintProseChanged(int Count) : BlueprintChange;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BlueprintChatAuthor), "chat")]
[JsonDerivedType(typeof(BlueprintTerminalAuthor), "terminal")]
public abstract record BlueprintAuthor;
public sealed record BlueprintChatAuthor(string SessionId) : BlueprintAuthor;
public sealed record BlueprintTerminalAuthor(string TabKey, string? AgentSessionId = null) : BlueprintAuthor;
public enum BlueprintPhase { Awaiting, Ready }
public sealed record BlueprintState(string WorkspaceId, BlueprintSource Source, string Brief, BlueprintAgentId AgentId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] BlueprintAuthor? Author, BlueprintPhase Phase, BlueprintDoc Doc, IReadOnlyList<BlueprintChange> Changes,
    IReadOnlyList<BlueprintEdit> PendingEdits, IReadOnlyDictionary<string, BlueprintBlockLines> Lines);
public sealed record BlueprintChangedPayload(string WorkspaceId, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] BlueprintState? State);