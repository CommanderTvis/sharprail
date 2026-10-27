using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.GitHub;

public sealed record GitHubSettings;

public sealed record GitHubSignInQuery;

/// <summary>Whether <c>gh</c> is signed in on the host, with the line to show.</summary>
public sealed record GitHubSignIn(bool Connected, string Detail);

public static class GitHubContract
{
    public const string Id = "github";

    public static readonly PluginMethod<GitHubSignInQuery, GitHubSignIn> SignIn = new("signIn");
    public static readonly PluginContract Contract = PluginContract.Create<GitHubSettings>(Id, 1, [SignIn], []);
    public static readonly PluginManifest Manifest = new(Id, "GitHub", "git-pull-request", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.VersionControl,
        Description = "Pull requests and GitHub sign-in for github.com remotes through the gh command-line tool: lookup, open, update, ready, merge and reopen.",
        EnabledByDefault = true,
        Host = "SharpRail.Plugins.GitHub.Host.dll",
        Ui = "SharpRail.Plugins.GitHub.UI.dll"
    };
}