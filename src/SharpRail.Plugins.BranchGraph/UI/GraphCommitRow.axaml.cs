using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.BranchGraph.UI;

internal sealed partial class GraphCommitRow : Button
{
    private readonly GraphLabels labels = new();
    internal GraphRow Model { get; private set; }
    internal LaneArt Lanes { get; }

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
                var hash = ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "GraphCopyHash");
                var patch = ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "GraphCopyPatch");
                hash.Icon = Icon("git-commit-line");
                patch.Icon = Icon("file-code-line");
                hash.Click += (_, _) => copyHash(Model.Commit.Sha);
                patch.Click += (_, _) => copyPatch(Model.Commit.Sha);
            }
            ContextMenu.Open(this);
            e.Handled = true;
        };
        Click += (_, _) => context.SetDiffScope(workspace(), new CommitDiffScope(Model.Commit.Sha));
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
        using var stream = AssetLoader.Open(new Uri($"avares://SharpRail.Plugins.BranchGraph.UI/Assets/{name}.svg"));
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return new(bytes.ToArray(), 16, Ui.TextBrush);
    }
}