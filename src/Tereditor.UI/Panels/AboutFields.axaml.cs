using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.UI.Panels;

/// <summary>
/// The About content: brand, name, version, runtime lines, links, license and the two actions. It reads
/// <see cref="AboutInfo"/> only, which holds no I/O; the notices lookup runs on a worker.
/// </summary>
public sealed partial class AboutFields : UserControl
{
    public AboutFields() : this(AboutInfo.Version, AboutInfo.Runtime()) { }

    public AboutFields(string version, IReadOnlyList<(string Label, string Value)> runtime)
    {
        AvaloniaXamlLoader.Load(this);
        Details = AboutInfo.Details(version, runtime);
        this.FindControl<ContentControl>("AboutBrand")!.Content = Ui.Icon("brand", Ui.Accent, 48);
        this.FindControl<TextBlock>("AboutName")!.Text = AboutInfo.Name;
        Muted("AboutVersion").Text = "Version " + version;
        this.FindControl<TextBlock>("AboutDescription")!.Text = AboutInfo.Description;
        Muted("AboutRuntime").Text = string.Join('\n', runtime.Select(line => $"{line.Label}: {line.Value}"));
        Muted("AboutLicense").Text = AboutInfo.License;
        Link("AboutRepository", "Source repository ↗", AboutInfo.Repository);
        Link("AboutUpstream", "Based on ThinkRail ↗", AboutInfo.Upstream);
        var issue = Link("AboutIssue", "Report an issue ↗", AboutInfo.NewIssue, open: false);
        issue.Click += (_, _) => { if (TopLevel.GetTopLevel(issue) is { } top) ReportIssue(top); };
        var actions = this.FindControl<WrapPanel>("AboutActions")!;
        var notices = Ui.Button("Third-party notices", () => _ = OpenNotices());
        notices.Name = "AboutNotices";
        var copy = Ui.Button("Copy details", () => _ = CopyDetails());
        copy.Name = "AboutCopy";
        notices.Margin = new(0, 0, 8, 0);
        actions.Children.Add(notices); actions.Children.Add(copy);
    }

    /// <summary>The plain text Copy details puts on the clipboard.</summary>
    public string Details { get; }

    private TextBlock Muted(string name)
    {
        var text = this.FindControl<TextBlock>(name)!;
        text.Foreground = Ui.Muted;
        return text;
    }

    /// <summary>Opens the repository's new-issue page; About's Report an issue link and the Help menu both call this.</summary>
    internal static void ReportIssue(TopLevel top) => _ = LanguageProviderLinks.Launch(top, new Uri(AboutInfo.NewIssue));

    private Button Link(string name, string label, string url, bool open = true)
    {
        var button = this.FindControl<Button>(name)!;
        button.Content = Ui.Text(label, Ui.Accent);
        button.Tag = new Uri(url);
        ToolTip.SetTip(button, url);
        if (open) button.Click += LanguageProviderLinks.Open;
        return button;
    }

    private async Task CopyDetails()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(Details);
    }

    /// <summary>Opens the shipped notices file when the package carries it, else the repository copy.</summary>
    private async Task OpenNotices()
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is not { } launcher) return;
        var path = await Task.Run(AboutInfo.FindNotices);
        await launcher.LaunchUriAsync(path is null ? new Uri(AboutInfo.NoticesUrl) : new Uri(path));
    }
}