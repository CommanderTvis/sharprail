using Avalonia.Controls;
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
        Require(links.Contains(AboutInfo.Repository) && links.Contains(AboutInfo.Upstream), "The links must target the repository and the upstream fork.");

        // The macOS application menu: About SharpRail in place of the framework's About Avalonia.
        var menu = ApplicationMenu.AppMenu(() => { });
        var headers = menu.Items.OfType<NativeMenuItem>().Select(item => item.Header ?? "").ToList();
        Require(headers.SequenceEqual(["About SharpRail"]), "The application menu must hold exactly About SharpRail.");
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