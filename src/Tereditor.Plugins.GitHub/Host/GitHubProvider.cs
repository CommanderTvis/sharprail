using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;

using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.GitHub.Host;

/// <summary>Runs a tool with the host's bounded runner; <see cref="IPluginHostContext.RunToolAsync"/> in the product.</summary>
public delegate ValueTask<RepoHostToolResult> ToolRunner(string tool, string directory, IReadOnlyList<string> args, TimeSpan timeout,
    IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken);

/// <summary>GitHub behind the neutral provider contract: github.com remotes, through the <c>gh</c> command-line tool.</summary>
public sealed class GitHubProvider(ToolRunner run) : RepoHostProvider
{

    private static readonly TimeSpan Query = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Mutation = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MergeMethodsTtl = TimeSpan.FromMinutes(10);
    private const int CompareBodyLimit = 4000;
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<string> Methods, long At)> MergeMethodCache = new();
    private static readonly Dictionary<string, string?> GhEnvironment = new() { ["GH_PROMPT_DISABLED"] = "1", ["GIT_TERMINAL_PROMPT"] = "0", ["NO_COLOR"] = "1" };

    private static bool Offline => Environment.GetEnvironmentVariable("SHARPRAIL_GH_OFFLINE") == "1";

    public override bool Recognizes(string remoteUrl) => Repository(remoteUrl) is not null;

    public override async ValueTask<RepoHostSignIn> SignInAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await run("gh", Environment.CurrentDirectory, ["auth", "status"], TimeSpan.FromSeconds(10), GhEnvironment, cancellationToken);
            var text = (result.Output + result.Error).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return result.ExitCode == 0 ? new(true, text) : new(false, "gh is installed but not signed in. Run gh auth login.");
        }
        catch (Win32Exception) { return new(false, "The gh command-line tool was not found on PATH."); }
        catch (TimeoutException) { return new(false, "gh did not respond."); }
    }

    public override async ValueTask<PullRequest?> FindAsync(RepoHostBranch branch, CancellationToken cancellationToken)
    {
        if (Offline) return null;
        // The open request first, else the newest in any state.
        var result = await run("gh", branch.Directory, ["pr", "list", "--head", branch.Branch, "--state", "all", "--limit", "20", "--json",
            "number,title,url,state,isDraft,statusCheckRollup,reviewDecision,mergeStateStatus,headRepositoryOwner,headRepository"], Query, GhEnvironment, cancellationToken);
        if (result.ExitCode != 0) throw new IOException("gh pr list failed.");
        using var json = JsonDocument.Parse(result.Output);
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0) return null;
        var items = json.RootElement.EnumerateArray().ToArray();
        var pr = items.FirstOrDefault(item => Text(item, "state") == "OPEN");
        if (pr.ValueKind != JsonValueKind.Object) pr = items[0];
        var url = Text(pr, "url");
        // Only a plain https link reaches a client that opens it in the system browser.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        var owner = pr.TryGetProperty("headRepositoryOwner", out var ownerElement) ? Text(ownerElement, "login") : "";
        var name = pr.TryGetProperty("headRepository", out var repositoryElement) ? Text(repositoryElement, "name") : "";
        // gh matches --head by branch name only, so a fork's same-named branch must not pass as this one.
        var same = string.Equals(owner + "/" + name, Repository(branch.RemoteUrl), StringComparison.OrdinalIgnoreCase);
        return new(pr.GetProperty("number").GetInt32(), Text(pr, "title"), url, Text(pr, "state").ToLowerInvariant(),
            pr.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True, ChecksState(pr),
            Text(pr, "reviewDecision") switch { "APPROVED" => "approved", "CHANGES_REQUESTED" => "changes-requested", "REVIEW_REQUIRED" => "required", _ => "none" },
            Text(pr, "mergeStateStatus") switch { "CLEAN" or "UNSTABLE" or "HAS_HOOKS" => "clean", "BLOCKED" => "blocked", "BEHIND" => "behind", "DIRTY" => "conflicting", _ => "unknown" },
            await MergeMethodsAsync(branch, cancellationToken), same);
    }

    public override async ValueTask<RepoHostOutcome> CreateOrUpdateAsync(RepoHostBranch branch, RepoHostRequest request, CancellationToken cancellationToken)
    {
        if (Offline) return new("failed", "", 0);
        try
        {
            if (await FindOpenAsync(branch, cancellationToken) is { } existing)
            {
                var edit = new List<string> { "pr", "edit", existing.Number.ToString(), "--body", request.Body };
                if (request.TitleEdited) edit.AddRange(["--title", request.Title]);
                await MutateAsync(branch.Directory, edit, cancellationToken);
                return new("updated", existing.Url, existing.Number);
            }
            var create = new List<string> { "pr", "create", "--base", branch.BaseName, "--head", branch.Branch, "--title", request.Title, "--body", request.Body };
            if (request.Draft) create.Add("--draft");
            string? created = null;
            try { created = (await MutateAsync(branch.Directory, create, cancellationToken)).Output.Trim().Split('\n').LastOrDefault()?.Trim(); }
            catch (Exception error) when (error is IOException or TimeoutException)
            {
                // A create that failed after the server accepted it still counts, so look again.
                if (await FindOpenAsync(branch, cancellationToken) is { } raced) return new("created", raced.Url, raced.Number);
                throw;
            }
            var made = await FindOpenAsync(branch, cancellationToken);
            return new("created", made?.Url ?? created ?? "", made?.Number ?? 0);
        }
        catch (Exception error) when (error is IOException or TimeoutException or Win32Exception or JsonException)
        {
            return new("failed", "", 0, await ProblemAsync(branch.Directory, cancellationToken));
        }
    }

    public override async ValueTask ReadyAsync(RepoHostBranch branch, PullRequest request, CancellationToken cancellationToken) =>
        await MutateAsync(branch.Directory, ["pr", "ready", request.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)], cancellationToken);

    public override async ValueTask ReopenAsync(RepoHostBranch branch, PullRequest request, CancellationToken cancellationToken) =>
        await MutateAsync(branch.Directory, ["pr", "reopen", request.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)], cancellationToken);

    public override async ValueTask MergeAsync(RepoHostBranch branch, PullRequest request, string method, string head, CancellationToken cancellationToken) =>
        await MutateAsync(branch.Directory, ["pr", "merge", request.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--" + method, "--match-head-commit", head], cancellationToken);

    public override string CompareUrl(RepoHostBranch branch, RepoHostRequest request)
    {
        var body = request.Body.Length > CompareBodyLimit ? request.Body[..CompareBodyLimit] : request.Body;
        return $"https://github.com/{Repository(branch.RemoteUrl)}/compare/{Uri.EscapeDataString(branch.BaseName)}...{Uri.EscapeDataString(branch.Branch)}" +
               $"?quick_pull=1&title={Uri.EscapeDataString(request.Title)}&body={Uri.EscapeDataString(body)}";
    }

    private async Task<(int Number, string Url)?> FindOpenAsync(RepoHostBranch branch, CancellationToken ct)
    {
        var args = new List<string> { "pr", "list", "--head", branch.Branch, "--state", "open", "--json", "number,url", "--limit", "1", "--base", branch.BaseName };
        var result = await run("gh", branch.Directory, args, Query, GhEnvironment, ct);
        if (result.ExitCode != 0) throw new IOException("gh pr list failed.");
        using var json = JsonDocument.Parse(result.Output);
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0) return null;
        var first = json.RootElement[0];
        var url = first.GetProperty("url").GetString() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        return (first.GetProperty("number").GetInt32(), url);
    }

    private async Task<RepoHostToolResult> MutateAsync(string directory, IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await run("gh", directory, args, Mutation, GhEnvironment, ct);
        if (result.ExitCode != 0) throw new IOException("gh failed: " + result.Error.Trim());
        return result;
    }

    private async Task<string> ProblemAsync(string directory, CancellationToken ct)
    {
        try { return (await run("gh", directory, ["auth", "status"], Query, GhEnvironment, ct)).ExitCode == 0 ? "" : "unauthenticated"; }
        catch (Win32Exception) { return "missing"; }
        catch (TimeoutException) { return "unauthenticated"; }
    }

    private async Task<IReadOnlyList<string>> MergeMethodsAsync(RepoHostBranch branch, CancellationToken ct)
    {
        try { return await ReadMergeMethodsAsync(branch, ct); }
        catch (Exception error) when (error is TimeoutException or IOException or Win32Exception or JsonException) { return []; }
    }

    private async Task<IReadOnlyList<string>> ReadMergeMethodsAsync(RepoHostBranch branch, CancellationToken ct)
    {
        var origin = branch.RemoteUrl;
        if (MergeMethodCache.TryGetValue(origin, out var cached) && Environment.TickCount64 - cached.At < MergeMethodsTtl.TotalMilliseconds) return cached.Methods;
        var result = await run("gh", branch.Directory, ["repo", "view", "--json", "mergeCommitAllowed,squashMergeAllowed,rebaseMergeAllowed"], Query, GhEnvironment, ct);
        if (result.ExitCode != 0) return [];
        using var json = JsonDocument.Parse(result.Output);
        bool Allowed(string name) => json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        IReadOnlyList<string> methods = [.. new[] { ("merge", "mergeCommitAllowed"), ("squash", "squashMergeAllowed"), ("rebase", "rebaseMergeAllowed") }
            .Where(pair => Allowed(pair.Item2)).Select(pair => pair.Item1)];
        MergeMethodCache[origin] = (methods, Environment.TickCount64);
        return methods;
    }

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>failing when any check failed, else pending while any runs, else passing when there are checks.</summary>
    private static string ChecksState(JsonElement pr)
    {
        if (!pr.TryGetProperty("statusCheckRollup", out var rollup) || rollup.ValueKind != JsonValueKind.Array || rollup.GetArrayLength() == 0) return "none";
        var pending = false;
        foreach (var check in rollup.EnumerateArray())
        {
            var conclusion = Text(check, "conclusion");
            var status = Text(check, "status");
            var state = Text(check, "state");
            if (conclusion is "FAILURE" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED" or "STARTUP_FAILURE" || state is "FAILURE" or "ERROR") return "failing";
            if (status.Length > 0 && status != "COMPLETED" || state is "PENDING" or "EXPECTED") pending = true;
        }
        return pending ? "pending" : "passing";
    }

    /// <summary>The owner/name of a github.com remote in https, scp or ssh form.</summary>
    internal static string? Repository(string origin)
    {
        string path;
        if (origin.StartsWith("git@github.com:", StringComparison.Ordinal)) path = origin["git@github.com:".Length..];
        else if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.Scheme is "https" or "ssh")
            path = uri.AbsolutePath.TrimStart('/');
        else return null;
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        var parts = path.Split('/');
        return parts.Length == 2 && parts.All(part => part.Length > 0) ? path : null;
    }
}