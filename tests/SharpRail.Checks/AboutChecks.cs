using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.UI;
using SharpRail.UI.Panels;

namespace SharpRail.Checks;

internal static class AboutChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run()
    {
        // A build without a stamp reads "dev"; a stamped one reads as stamped.
        Require(AboutInfo.ParseVersion(null) == "dev" && AboutInfo.ParseVersion("") == "dev" && AboutInfo.ParseVersion("  ") == "dev", "Missing version text must read dev.");
        Require(AboutInfo.ParseVersion("0.2.0+20260101-abc1234") == "0.2.0+20260101-abc1234", "A stamped version must be shown as stamped.");
        Require(AboutInfo.Version.Length > 0, "The assembly version must not be empty.");

        var runtime = AboutInfo.Runtime();
        Require(runtime.Any(line => line.Label == "Runtime") && runtime.Any(line => line.Label == "OS") && runtime.Any(line => line.Label == "Avalonia"), "Runtime lines must name the runtime, OS and Avalonia.");
        var details = AboutInfo.Details("9.9.9-test", runtime);
        Require(details.StartsWith("SharpRail 9.9.9-test\n", StringComparison.Ordinal) && runtime.All(line => details.Contains($"{line.Label}: {line.Value}")), "Copy details must carry the name, version and every runtime line.");

        var fields = new AboutFields("9.9.9-test", runtime);
        var texts = fields.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").ToList();
        Require(texts.Contains("SharpRail") && texts.Contains("Version 9.9.9-test") && texts.Contains(AboutInfo.Description), "The About view must show name, version and description.");
        Require(texts.Any(text => text.Contains("Runtime: .NET") && text.Contains("OS: ") && text.Contains("Avalonia: ")), "The About view must show the runtime lines.");
        Require(fields.Details == details, "The view's copy text must equal the details text.");
        var buttons = fields.GetLogicalDescendants().OfType<Button>().Select(button => button.Name).ToList();
        Require(buttons.Contains("AboutNotices") && buttons.Contains("AboutCopy") && buttons.Contains("AboutRepository") && buttons.Contains("AboutUpstream"), "The About view must offer notices, copy and both links.");
        var links = fields.GetLogicalDescendants().OfType<Button>().Where(button => button.Tag is Uri).Select(button => ((Uri)button.Tag!).AbsoluteUri.TrimEnd('/')).ToList();
        Require(links.Contains(AboutInfo.Repository) && links.Contains(AboutInfo.Upstream) && links.Contains(AboutInfo.NewIssue), "The links must target the repository, upstream ThinkRail and the new-issue page.");

        // Report an issue is the primary way the Help menu's Report an Issue… follows (`--shell` picks that item): both open the new-issue page through the shared launch.
        var launched = new List<(TopLevel Top, Uri Uri)>();
        var launch = LanguageProviderLinks.Launch;
        LanguageProviderLinks.Launch = (top, uri) => { launched.Add((top, uri)); return Task.CompletedTask; };
        try
        {
            var host = new Window { Content = fields };
            host.Show();
            var issue = fields.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "AboutIssue");
            Require(issue.IsVisible && ((Uri)issue.Tag!).AbsoluteUri == AboutInfo.NewIssue, "Report an issue must show and target AboutInfo.NewIssue.");
            issue.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Require(launched.Count == 1 && launched[0].Top == host && launched[0].Uri.AbsoluteUri == AboutInfo.NewIssue, "Report an issue must open the new-issue page from its window.");
            host.Close();
        }
        finally { LanguageProviderLinks.Launch = launch; }

        // The macOS application menu: About SharpRail in place of the framework's About Avalonia.
        var settings = 0;
        var menu = ApplicationMenu.AppMenu(() => { }, () => settings++);
        var headers = menu.Items.OfType<NativeMenuItem>().Where(item => item is not NativeMenuItemSeparator).Select(item => item.Header ?? "").ToList();
        Require(headers.SequenceEqual(["About SharpRail", "Settings…"]) && menu.Items[1] is NativeMenuItemSeparator, "The application menu must hold About SharpRail, then Settings.");
        var preferences = menu.Items.OfType<NativeMenuItem>().Single(item => item.Header == "Settings…");
        Require(preferences.Gesture is { } chord && chord.Equals(WindowShortcuts.Settings.Gesture), "Settings shows the window's Settings chord.");
        ((INativeMenuItemExporterEventsImplBridge)preferences).RaiseClicked();
        Require(settings == 1, "Settings runs its command.");
        Require(!headers.Any(header => header.Contains("Avalonia", StringComparison.OrdinalIgnoreCase)), "The application menu must not offer About Avalonia.");

        // The dialog closes with Escape.
        var window = Dialogs.About(null);
        Dispatcher.UIThread.RunJobs();
        Require(window.IsVisible && window.GetLogicalDescendants().OfType<AboutFields>().Any(), "The About dialog must show the About view.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Require(!window.IsVisible, "Escape must close the About dialog.");
    }
}