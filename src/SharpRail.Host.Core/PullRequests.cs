using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    private static readonly TimeSpan LookupTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GhQuery = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan GhMutation = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PushBudget = TimeSpan.FromSeconds(55);
    private const int CompareBodyLimit = 4000;

    private sealed class Lookup(Task<(OpenReview? Review, bool Ok)> task)
    {
        internal Task<(OpenReview? Review, bool Ok)> Task { get; } = task;
        internal long CompletedAt;
    }

    private static readonly ConcurrentDictionary<string, Lookup> Lookups = new();

    private static readonly Dictionary<string, string?> GhEnvironment = new() { ["GH_PROMPT_DISABLED"] = "1", ["GIT_TERMINAL_PROMPT"] = "0", ["NO_COLOR"] = "1" };

    public async ValueTask<OpenReview?> GetOpenReviewAsync(bool fresh, CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        var branch = await CurrentBranchAsync(currentRoot, cancellationToken);
        if (branch is null) return null;
        var key = currentRoot + "\0" + branch;
        Lookup entry;
        lock (Lookups)
        {
            var now = Environment.TickCount64;
            if (Lookups.TryGetValue(key, out var existing) &&
                (!existing.Task.IsCompleted || !fresh && existing.CompletedAt != 0 && now - existing.CompletedAt < LookupTtl.TotalMilliseconds))
                entry = existing;
            else
            {
                entry = new Lookup(Task.Run(() => LookupOpenReviewAsync(currentRoot, branch, fresh)));
                Lookups[key] = entry;
                var created = entry;
                _ = created.Task.ContinueWith(task =>
                {
                    lock (Lookups)
                    {
                        if (task.IsCompletedSuccessfully && task.Result.Ok) created.CompletedAt = Environment.TickCount64;
                        else Lookups.TryRemove(new KeyValuePair<string, Lookup>(key, created));
                    }
                }, TaskScheduler.Default);
            }
        }
        var (review, _) = await entry.Task.WaitAsync(cancellationToken);
        return review;
    }

    private static void DropLookups(string currentRoot, string branch) => Lookups.TryRemove(currentRoot + "\0" + branch, out _);

    private static async Task<(OpenReview? Review, bool Ok)> LookupOpenReviewAsync(string currentRoot, string branch, bool fresh)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("SHARPRAIL_GH_OFFLINE") == "1" || !IsGitHub(await OriginUrlAsync(currentRoot, CancellationToken.None)))
                return (null, true);
            var found = await FindPullRequestAsync(currentRoot, branch, null, CancellationToken.None);
            if (found is null) return (null, true);
            var unpushed = await CountAsync(currentRoot, $"origin/{branch}..HEAD");
            // Behind is only trustworthy against a ref that was just fetched.
            var behind = -1;
            if (fresh)
            {
                try
                {
                    var fetch = await ChildProcess.RunAsync("git", currentRoot, ["fetch", "--quiet", "--no-tags", "--end-of-options", "origin", branch],
                        TimeSpan.FromSeconds(20), CancellationToken.None, GitPromptFree);
                    if (fetch.ExitCode == 0) behind = await CountAsync(currentRoot, $"HEAD..origin/{branch}");
                }
                catch (Exception error) when (error is TimeoutException or IOException or Win32Exception) { }
            }
            return (new OpenReview(found.Value.Number, found.Value.Url, "github", unpushed, behind), true);
        }
        catch (Exception error) when (error is TimeoutException or IOException or Win32Exception or JsonException)
        {
            return (null, false);
        }
    }

    private static async Task<int> CountAsync(string currentRoot, string range)
    {
        try { return int.Parse((await GitRepository.RunAsync(currentRoot, CancellationToken.None, "rev-list", "--count", range)).Trim()); }
        catch (IOException) { return -1; }
    }

    private static readonly Dictionary<string, string?> GitPromptFree = new() { ["GIT_TERMINAL_PROMPT"] = "0" };

    private static async Task<(int Number, string Url)?> FindPullRequestAsync(string currentRoot, string branch, string? baseName, CancellationToken ct)
    {
        var args = new List<string> { "pr", "list", "--head", branch, "--state", "open", "--json", "number,url", "--limit", "1" };
        if (baseName is not null) args.AddRange(["--base", baseName]);
        var result = await ChildProcess.RunAsync("gh", currentRoot, args, GhQuery, ct, GhEnvironment);
        if (result.ExitCode != 0) throw new IOException("gh pr list failed.");
        using var json = JsonDocument.Parse(result.Output);
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0) return null;
        var first = json.RootElement[0];
        var url = first.GetProperty("url").GetString() ?? "";
        // Only a plain https link reaches a client that opens it in the system browser.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        return (first.GetProperty("number").GetInt32(), url);
    }

    public async ValueTask<PrDraft> PreviewPrAsync(CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        var branch = await CurrentBranchAsync(currentRoot, cancellationToken) ?? "";
        var body = "";
        try
        {
            var baseRef = await DefaultBaseAsync(currentRoot, cancellationToken);
            var subjects = (await GitRepository.RunAsync(currentRoot, cancellationToken, "log", "--format=- %s", "--max-count=20", "--end-of-options", $"{baseRef}..HEAD")).Trim();
            body = subjects;
        }
        catch (IOException) { }
        return new(branch, body);
    }

    public async ValueTask<PrResult> OpenPrAsync(PrRequest request, CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        var branch = await CurrentBranchAsync(currentRoot, cancellationToken) ?? throw new InvalidOperationException("A detached HEAD has no branch to open a pull request for.");
        if (branch.StartsWith('-')) throw new ArgumentException("Refusing a branch name shaped like an option.");
        if (!GitRefs.IsSafe(branch)) throw new ArgumentException("The current branch name is not a valid ref.");
        if (!(await RemotesAsync(currentRoot, cancellationToken)).Contains("origin"))
            throw new InvalidOperationException("Pull requests are opened against the origin remote.");
        var baseRef = await DefaultBaseAsync(currentRoot, cancellationToken);
        if (!baseRef.StartsWith("origin/", StringComparison.Ordinal))
            throw new InvalidOperationException("The comparison base is not on origin; refusing to push for a pull request.");
        var baseName = baseRef[7..];
        if (branch == baseName) throw new InvalidOperationException("Refusing to push the default branch; create a branch first.");
        var dirty = (await GitRepository.RunAsync(currentRoot, cancellationToken, "status", "--porcelain=v1", "-z", "--untracked-files=all"))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Count(entry => entry.Length > 3 && entry[2] == ' ');
        if (await CurrentBranchAsync(currentRoot, cancellationToken) != branch)
            throw new InvalidOperationException("The workspace switched branches while the pull request was being prepared.");

        var pushed = await PushAsync(currentRoot, branch, cancellationToken);
        DropLookups(currentRoot, branch);
        if (pushed is not null) return new("authFailed", "", 0, dirty, "");
        var origin = await OriginUrlAsync(currentRoot, cancellationToken);
        if (!IsGitHub(origin)) return new("pushed", "", 0, dirty, "");
        if (Environment.GetEnvironmentVariable("SHARPRAIL_GH_OFFLINE") == "1")
            return new("compare", CompareUrl(origin, baseName, branch, request), 0, dirty, "");
        try
        {
            if (await FindPullRequestAsync(currentRoot, branch, baseName, cancellationToken) is { } existing)
            {
                var edit = new List<string> { "pr", "edit", existing.Number.ToString(), "--body", request.Body };
                if (request.TitleEdited) edit.AddRange(["--title", request.Title]);
                await GhMutationAsync(currentRoot, edit, cancellationToken);
                return new("updated", existing.Url, existing.Number, dirty, "");
            }
            var create = new List<string> { "pr", "create", "--base", baseName, "--head", branch, "--title", request.Title, "--body", request.Body };
            if (request.Draft) create.Add("--draft");
            string? created = null;
            try { created = (await GhMutationAsync(currentRoot, create, cancellationToken)).Output.Trim().Split('\n').LastOrDefault()?.Trim(); }
            catch (Exception error) when (error is IOException or TimeoutException)
            {
                // A create that failed after the server accepted it still counts, so look again.
                if (await FindPullRequestAsync(currentRoot, branch, baseName, cancellationToken) is { } raced) return new("created", raced.Url, raced.Number, dirty, "");
                throw;
            }
            var made = await FindPullRequestAsync(currentRoot, branch, baseName, cancellationToken);
            return new("created", made?.Url ?? created ?? "", made?.Number ?? 0, dirty, "");
        }
        catch (Exception error) when (error is IOException or TimeoutException or Win32Exception or JsonException)
        {
            return new("compare", CompareUrl(origin, baseName, branch, request), 0, dirty, await GhProblemAsync(currentRoot, cancellationToken));
        }
        finally { DropLookups(currentRoot, branch); }
    }

    private static async Task<ChildProcess.Result> GhMutationAsync(string currentRoot, IEnumerable<string> args, CancellationToken ct)
    {
        var result = await ChildProcess.RunAsync("gh", currentRoot, args, GhMutation, ct, GhEnvironment);
        if (result.ExitCode != 0) throw new IOException("gh failed: " + result.Error.Trim());
        return result;
    }

    private static async Task<string> GhProblemAsync(string currentRoot, CancellationToken ct)
    {
        try { return (await ChildProcess.RunAsync("gh", currentRoot, ["auth", "status"], GhQuery, ct, GhEnvironment)).ExitCode == 0 ? "" : "unauthenticated"; }
        catch (Win32Exception) { return "missing"; }
        catch (TimeoutException) { return "unauthenticated"; }
    }

    /// <summary>Pushes the branch with prompts disabled; returns null on success and the stderr when authentication failed.</summary>
    private static async Task<string?> PushAsync(string currentRoot, string branch, CancellationToken ct)
    {
        var env = new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_MESSAGES"] = "C" };
        // LC_ALL would override LC_MESSAGES, so its character-set meaning moves to LC_CTYPE.
        if (Environment.GetEnvironmentVariable("LC_ALL") is { Length: > 0 } all)
        {
            env["LC_ALL"] = null;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LC_CTYPE"))) env["LC_CTYPE"] = all;
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH_COMMAND")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH")) &&
            string.IsNullOrWhiteSpace(await ConfigAsync(currentRoot, "core.sshCommand", ct)))
            env["GIT_SSH_COMMAND"] = "ssh -oBatchMode=yes";
        ChildProcess.Result result;
        try { result = await ChildProcess.RunAsync("git", currentRoot, ["push", "--set-upstream", "origin", "--end-of-options", branch], PushBudget, ct, env); }
        catch (TimeoutException) { throw new IOException("git push timed out; check the network and that your SSH keys are loaded."); }
        if (result.ExitCode == 0) return null;
        var text = result.Error;
        if (new[] { "publickey", "Username", "Authentication failed", "terminal prompts disabled", "Host key verification failed" }.Any(text.Contains))
            return text;
        throw new IOException("git push failed: " + text.Trim());
    }

    private static async Task<string> ConfigAsync(string currentRoot, string key, CancellationToken ct)
    {
        try { return await GitRepository.RunAsync(currentRoot, ct, "config", "--get", key); }
        catch (IOException) { return ""; }
    }

    private static async Task<string?> CurrentBranchAsync(string currentRoot, CancellationToken ct)
    {
        try
        {
            var branch = (await GitRepository.RunAsync(currentRoot, ct, "symbolic-ref", "--short", "-q", "HEAD")).Trim();
            return branch.Length == 0 ? null : branch;
        }
        catch (IOException) { return null; }
    }

    private static async Task<string> OriginUrlAsync(string currentRoot, CancellationToken ct)
    {
        try { return (await GitRepository.RunAsync(currentRoot, ct, "remote", "get-url", "origin")).Trim(); }
        catch (IOException) { return ""; }
    }

    private static bool IsGitHub(string origin) => GitHubRepository(origin) is not null;

    /// <summary>The owner/name of a github.com remote in https, scp or ssh form.</summary>
    private static string? GitHubRepository(string origin)
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

    private static string CompareUrl(string origin, string baseName, string branch, PrRequest request)
    {
        var body = request.Body.Length > CompareBodyLimit ? request.Body[..CompareBodyLimit] : request.Body;
        return $"https://github.com/{GitHubRepository(origin)}/compare/{Uri.EscapeDataString(baseName)}...{Uri.EscapeDataString(branch)}" +
               $"?quick_pull=1&title={Uri.EscapeDataString(request.Title)}&body={Uri.EscapeDataString(body)}";
    }
}