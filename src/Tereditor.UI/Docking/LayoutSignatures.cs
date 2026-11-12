namespace Tereditor.UI.Docking;

// What a rebuilt view depends on, written as JSON so an unchanged view is kept rather than rebuilt.
internal sealed record PreviewGroupSignature(string Group, List<DockTab>? Tabs, string[]? Decorations, string? Selected, List<DockPane>? Panes);

internal sealed record PreviewTerminalSignature(DockTab Tab, string Decoration);

internal sealed record DrawerSectionSignature(string Id, string? Group, string Title);

internal sealed record DrawerSignature(string? Open, DrawerSectionSignature[] Sections);

internal sealed record GroupSignature(IReadOnlyList<DockTab> Tabs, DockToolInfo?[] Tools, List<DockPane>? Panes);