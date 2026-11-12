using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.BranchGraph.UI;

internal sealed partial class GraphCommitRow : Button
{
    private readonly GraphLabels labels = new();
    private string? branchTarget;
    internal GraphRow Model { get; private set; }
    internal LaneArt Lanes { get; }
    internal string? BranchTarget => branchTarget;
    internal string? LocalBranchAt(Avalonia.Point point) => labels.LocalBranchAt(point);

    internal GraphCommitRow(IPluginUIContext context, Func<string> workspace, GraphRow model, int drawn, int shown,
        IReadOnlyList<string> marks, Action<string> copyHash, Action<string> copyPatch)
    {
        Model = model;
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ContentControl>("GraphLabelsHost")!.Content = labels;
        Lanes = new(model, drawn, shown, marks.Count > 0);
        this.FindControl<ContentControl>("GraphLaneHost")!.Content = Lanes;
        ContextRequested += (_, e) =>
        {
            if (ContextMenu is null)
            {
                ContextMenu = (ContextMenu)((IDataTemplate)Resources["GraphMenuTemplate"]!).Build(null)!;
                var changes = ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "GraphViewChanges");
                changes.Icon = Icon("file-code-line");
                changes.Click += (_, _) => context.OpenCommitChanges(workspace(), Model.Commit.Sha);
                var hash = ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "GraphCopyHash");
                var patch = ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "GraphCopyPatch");
                hash.Icon = Icon("git-commit-line");
                patch.Icon = Icon("file-code-line");
                hash.Click += (_, _) => copyHash(Model.Commit.Sha);
                patch.Click += (_, _) => copyPatch(Model.Commit.Sha);
                ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "GraphDeleteBranch").Click += (_, _) =>
                {
                    if (branchTarget is { } name) _ = context.DeleteBranchAsync(name).AsTask();
                };
            }
            // Only a local branch label offers deletion; the app's own flow confirms, refuses and offers the undo.
            branchTarget = e.TryGetPosition(labels, out var point) ? labels.LocalBranchAt(point) : null;
            ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "GraphDeleteBranch").IsVisible = branchTarget is not null;
            ContextMenu.Items.OfType<Separator>().Single(item => item.Name == "GraphBranchSeparator").IsVisible = branchTarget is not null;
            ContextMenu.Open(this);
            e.Handled = true;
        };
        Click += (_, _) =>
        {
            context.SetDiffScope(workspace(), new CommitDiffScope(Model.Commit.Sha));
            context.OpenCommitChanges(workspace(), Model.Commit.Sha);
        };
        Update(model, drawn, shown, marks);
    }

    internal void Update(GraphRow model, int drawn, int shown, IReadOnlyList<string> marks)
    {
        Model = model;
        Tag = model.Commit.ShortSha;
        labels.Update(model.Commit, marks);
        Avalonia.Automation.AutomationProperties.SetName(this, $"{model.Commit.Subject}, {model.Commit.ShortSha}, {model.Commit.Author}");
        Avalonia.Automation.AutomationProperties.SetHelpText(this, string.Join(", ", model.Commit.Refs.Concat(marks)));
        Lanes.Update(model, drawn, shown, marks.Count > 0);
    }

    private static SvgAsset Icon(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Tereditor.Plugins.BranchGraph.UI/Assets/{name}.svg"));
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return new(bytes.ToArray(), 16, Ui.TextBrush);
    }
}