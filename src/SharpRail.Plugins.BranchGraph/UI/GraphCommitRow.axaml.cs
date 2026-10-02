using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.BranchGraph.UI;

internal sealed partial class GraphCommitRow : Button
{
    private readonly TextBlock subject, shortSha, author;
    private readonly ItemsControl refs, worktrees;
    internal GraphRow Model { get; private set; }
    internal LaneArt Lanes { get; }

    internal GraphCommitRow(IPluginUIContext context, Func<string> workspace, GraphRow model, int drawn, int shown,
        IReadOnlyList<string> marks, Action<string> copyHash, Action<string> copyPatch)
    {
        Model = model;
        AvaloniaXamlLoader.Load(this);
        subject = this.FindControl<TextBlock>("GraphSubject")!;
        shortSha = this.FindControl<TextBlock>("GraphShortSha")!;
        author = this.FindControl<TextBlock>("GraphAuthor")!;
        refs = this.FindControl<ItemsControl>("GraphRefs")!;
        worktrees = this.FindControl<ItemsControl>("GraphWorktrees")!;
        Lanes = new(model, drawn, shown, marks.Count > 0);
        this.FindControl<ContentControl>("GraphLaneHost")!.Content = Lanes;
        var hash = this.FindControl<MenuItem>("GraphCopyHash")!;
        var patch = this.FindControl<MenuItem>("GraphCopyPatch")!;
        hash.Icon = Icon("git-commit-line");
        patch.Icon = Icon("file-code-line");
        hash.Click += (_, _) => copyHash(Model.Commit.Sha);
        patch.Click += (_, _) => copyPatch(Model.Commit.Sha);
        Click += (_, _) => context.SetDiffScope(workspace(), new CommitDiffScope(Model.Commit.Sha));
        Update(model, drawn, shown, marks);
    }

    internal void Update(GraphRow model, int drawn, int shown, IReadOnlyList<string> marks)
    {
        Model = model;
        Tag = model.Commit.ShortSha;
        subject.Text = model.Commit.Subject;
        shortSha.Text = model.Commit.ShortSha;
        author.Text = model.Commit.Author;
        if (refs.ItemsSource is not IEnumerable<string> previousRefs || !previousRefs.SequenceEqual(model.Commit.Refs)) refs.ItemsSource = model.Commit.Refs;
        if (worktrees.ItemsSource is not IEnumerable<string> previousMarks || !previousMarks.SequenceEqual(marks)) worktrees.ItemsSource = marks;
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