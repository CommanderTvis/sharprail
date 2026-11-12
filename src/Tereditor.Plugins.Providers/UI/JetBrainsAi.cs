using System.Text.RegularExpressions;

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Providers.UI;

/// <summary>
/// JetBrains AI for Pi in a browser tab: the guide, the disclaimer and the token command, from the ThinkRail tab host. The
/// tab cannot run Central itself, so the user runs <see cref="TokenCommand"/> on their own computer and pastes the short-lived
/// token it prints; the tab sends it to JetBrains AI (Grazie) and keeps it no longer than the hour it is valid.
/// </summary>
internal static partial class JetBrainsAi
{
    public const string Id = "jetbrains-ai";
    public const string CentralDocs = "https://www.jetbrains.com/help/central-cli/quickstart.html";
    public const string Licences = "https://account.jetbrains.com/licenses";

    public const string Disclaimer = "This is unofficial software, not affiliated with or endorsed by JetBrains; its author takes no responsibility for what happens to your account, licence or tokens. It signs in with Central's login outside Central and talks to JetBrains AI from a client JetBrains did not make: JetBrains may treat that as a breach of its terms and could suspend your account or licence. You use it at your own risk.";

    [GeneratedRegex(@"^[A-Za-z0-9-]+$")]
    private static partial Regex LicenceShape();

    [GeneratedRegex(@"^[\w-]+\.[\w-]+\.[\w-]+$")]
    private static partial Regex TokenShape();

    public static bool IsLicenceId(string value) => LicenceShape().IsMatch(value);

    public static bool IsToken(string value) => TokenShape().IsMatch(value);

    public static string TokenCommand(string licenseId) => string.Join(' ',
        $"central login && LICENSE_ID={licenseId} &&",
        "curl -s https://api.jetbrains.ai/auth/jetbrains-jwt/provide-access/license/v2",
        "-H \"Authorization: Bearer $(curl -s https://oauth.account.jetbrains.com/oauth2/token",
        "-d grant_type=refresh_token -d client_id=central-cli",
        "-d refresh_token=\"$(central login export | jq -rR 'ltrimstr(\"central-\")|gsub(\"-\";\"+\")|gsub(\"_\";\"/\")|@base64d|fromjson|.refresh_token')\"",
        "| jq -r .id_token)\"",
        "-H 'Content-Type: application/json'",
        "-d \"{\\\"licenseId\\\":\\\"$LICENSE_ID\\\"}\" | jq -r .token");

    public static TextBlock Step(string text)
    {
        var block = Ui.Text(text, Ui.Muted, 12);
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    /// <summary>What the row says before anything is entered: the disclaimer, and where Central and the licences are.</summary>
    public static Control Notice()
    {
        var notice = new StackPanel { Spacing = 4 };
        var warning = Ui.Text(Disclaimer, Ui.Warning, 12);
        warning.TextWrapping = TextWrapping.Wrap;
        notice.Children.Add(warning);
        notice.Children.Add(Step("Uses your JetBrains AI licence through Central's sign-in, with Claude's models. A hosted page cannot sign in to JetBrains itself, so you run one command on your computer and paste its token; it lasts an hour."));
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (label, url) in new[] { ("Central CLI quickstart", CentralDocs), ("Your licences", Licences) })
        {
            var link = Ui.Button(label, () => { });
            link.Click += (_, _) => TopLevel.GetTopLevel(link)?.Launcher.LaunchUriAsync(new Uri(url));
            links.Children.Add(link);
        }
        notice.Children.Add(links);
        return notice;
    }
}