using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Tereditor.Plugins.Api.Host;

namespace Tereditor.Host.Core.Language;

/// <summary>
/// Consent to the terms of a language server in a host with no window and no terminal (headless). The desktop shows the terms of an installed server in a
/// window; here nobody can click, so consent comes from a tool the agent calls after the user agreed in chat, from the client's elicitation prompt, or from
/// an environment variable the user set when starting the host. Consent names the server, its version and the SHA-256 of the pinned terms text
/// (<see cref="LanguageServerSpec.TermsSha256"/>), once the archive was downloaded and its terms file matched that hash and the user saw the full text; when the provider has installed the pinned archive,
/// <see cref="LanguageServers.ProvisionAsync"/> checks that the text inside is that text and writes the same acceptance record the desktop writes.
/// Nothing is ever accepted by default.
/// </summary>
internal sealed class LanguageConsent(string storage, Func<IReadOnlyList<LanguageServer>> catalog, Func<string, string?> environment,
    Func<LanguageServer, bool, CancellationToken, Task<LanguageServerTerms>>? reader = null, Func<LanguageServer, string>? pinned = null)
{
    /// <summary>The environment variable listing servers to download at start, before anyone is asked: <c>intellij</c>, comma separated. A download executes nothing.</summary>
    internal const string PrefetchVariable = "TEREDITOR_PREFETCH";

    internal string PinnedHash(LanguageServer server) => Pinned(server);

    private string Pinned(LanguageServer server) => pinned?.Invoke(server) ?? server.Spec.TermsSha256;

    /// <summary>The environment variable listing servers the user accepts at start: <c>intellij</c>, or <c>intellij:&lt;terms hash&gt;</c> for strict mode, comma separated.</summary>
    internal const string EnvironmentVariable = "TEREDITOR_ACCEPT_TERMS";

    /// <summary>What a host may rely on to start a server: the terms hash consent was given to, and the answers to the provider's questions.</summary>
    internal sealed record Grant(string TextSha256, IReadOnlyDictionary<string, string> Answers, string Source);

    /// <summary>The file written when consent was given with a tool or a prompt.</summary>
    internal sealed record Stored(string Version, string TextSha256, Dictionary<string, string> Answers, string AcceptedAtUtc, string Via);

    private readonly object gate = new();
    private readonly Dictionary<string, Grant?> recorded = new(StringComparer.Ordinal);

    private string RecordPath(LanguageServer server) => Path.Combine(storage, "consents", server.Id + ".json");

    /// <summary>The servers that have terms to agree to before use and pin them, in roster order.</summary>
    internal IReadOnlyList<LanguageServer> Gated => [.. catalog().Where(server => server.Spec.TermsAsync is not null && server.Spec.TermsSha256.Length == 64)];

    internal LanguageServer? Find(string id) => Gated.FirstOrDefault(server => server.Id == id);

    private static bool Answered(LanguageServer server, IReadOnlyDictionary<string, string> answers) => LanguageTerms.Answered(server.Spec.TermsQuestions, answers);

    /// <summary>The grant for a server now, or null: consent recorded for these exact terms and version, else the environment variable.</summary>
    internal Grant? GrantFor(LanguageServer server)
    {
        if (server.Spec.TermsSha256.Length != 64 || server.Spec.TermsAsync is null) return null;
        lock (gate)
        {
            if (!recorded.TryGetValue(server.Id, out var known)) recorded[server.Id] = known = ReadRecord(server);
            if (known is not null) return known;
        }
        return FromEnvironment(server, null);
    }

    private Grant? ReadRecord(LanguageServer server)
    {
        try
        {
            var path = RecordPath(server);
            if (!File.Exists(path)) return null;
            var stored = JsonSerializer.Deserialize(File.ReadAllText(path), HostJson.Default.Stored);
            return stored is not null && stored.Version == server.Spec.Version && stored.TextSha256 == Pinned(server) && Answered(server, stored.Answers)
                ? new Grant(stored.TextSha256, stored.Answers, "recorded") : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>The environment's grant for a server; a problem with its entry goes to <paramref name="warnings"/> when given.</summary>
    private Grant? FromEnvironment(LanguageServer server, List<string>? warnings)
    {
        foreach (var raw in (environment(EnvironmentVariable) ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = raw.IndexOf(':');
            var id = colon < 0 ? raw : raw[..colon];
            if (!string.Equals(id, server.Id, StringComparison.Ordinal)) continue;
            if (colon >= 0 && !string.Equals(raw[(colon + 1)..], Pinned(server), StringComparison.OrdinalIgnoreCase))
            {
                warnings?.Add($"{EnvironmentVariable} names {server.Id} with another terms hash than this build's ({Pinned(server)}); it is not accepted.");
                return null;
            }
            var answers = new Dictionary<string, string>();
            foreach (var question in server.Spec.TermsQuestions)
            {
                var name = AnswerVariable(server, question);
                var value = environment(name)?.Trim();
                var match = value is null ? null : question.Values.FirstOrDefault(allowed => string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    warnings?.Add($"{EnvironmentVariable} accepts {server.Id}, but {name} is {(string.IsNullOrEmpty(value) ? "not set" : "not one of " + string.Join(", ", question.Values))}; it is not accepted.");
                    return null;
                }
                answers[question.Id] = match;
            }
            return new Grant(Pinned(server), answers, "environment");
        }
        return null;
    }

    /// <summary>The variable that answers a question, such as <c>INTELLIJ_REGION</c> or <c>INTELLIJ_DATA_SHARING</c> (the names the vendor's own server reads).</summary>
    internal static string AnswerVariable(LanguageServer server, LanguageServerQuestion question)
    {
        var name = new StringBuilder(server.Id.ToUpperInvariant()).Append('_');
        foreach (var character in question.Id)
        {
            if (char.IsUpper(character) && name[^1] != '_') name.Append('_');
            name.Append(char.ToUpperInvariant(character));
        }
        return name.ToString();
    }

    /// <summary>Problems with the environment variable, for the log at start: an unknown server, a hash that is not this build's, a missing answer.</summary>
    internal IReadOnlyList<string> EnvironmentWarnings()
    {
        var warnings = new List<string>();
        var gated = Gated;
        foreach (var raw in (environment(EnvironmentVariable) ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var id = raw.Contains(':') ? raw[..raw.IndexOf(':')] : raw;
            if (gated.All(server => server.Id != id)) warnings.Add($"{EnvironmentVariable} names {id}, which has no terms to accept in this build (servers with terms: {(gated.Count == 0 ? "none" : string.Join(", ", gated.Select(server => server.Id)))}).");
        }
        foreach (var server in gated) FromEnvironment(server, warnings);
        return warnings;
    }

    /// <summary>
    /// Records consent given by the user, through a tool or a prompt. Refuses a hash or version that is not the one this build pins, and answers outside the
    /// offered ones, and anything before the download finished and its terms file matched the pinned hash; the message says why.
    /// </summary>
    internal async Task<(bool Accepted, string Message)> AcceptAsync(LanguageServer server, string hash, string? version, IReadOnlyDictionary<string, string> answers, string via, CancellationToken token)
    {
        if (Prefetched(server).State != PrefetchState.Ready)
            return (false, $"Nothing was accepted: the download of {Name(server)} has not finished, so the full terms text has not been shown yet. Call accept_terms with only the server to see where it stands.");
        if (!string.Equals(hash.Trim(), Pinned(server), StringComparison.OrdinalIgnoreCase))
            return (false, $"The hash is not the current terms hash of {Name(server)}. Nothing was accepted. Read the terms again with accept_terms (server only, no hash) and ask the user about that text.");
        if (version is { Length: > 0 } && version != server.Spec.Version)
            return (false, $"The version is not the current version of {Name(server)} ({server.Spec.Version}). Nothing was accepted.");
        var clean = new Dictionary<string, string>();
        foreach (var question in server.Spec.TermsQuestions)
        {
            var match = answers.TryGetValue(question.Id, out var value) ? question.Values.FirstOrDefault(allowed => string.Equals(allowed, value?.Trim(), StringComparison.OrdinalIgnoreCase)) : null;
            if (match is null)
                return (false, $"Nothing was accepted: the user has not answered \"{question.Prompt}\". Ask the user, then send one of: {string.Join(", ", question.Values)}. Never choose for the user.");
            clean[question.Id] = match;
        }
        var stored = new Stored(server.Spec.Version, Pinned(server), clean, DateTimeOffset.UtcNow.ToString("O"), via);
        var path = RecordPath(server);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(stored, HostJson.Default.Stored), token);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (false, $"Nothing was accepted: the state directory cannot be written ({error.Message}).");
        }
        lock (gate) recorded[server.Id] = new Grant(stored.TextSha256, stored.Answers, "recorded");
        return (true, $"Recorded: the user accepted {server.Spec.TermsTitle} for {Name(server)} {server.Spec.Version} (terms hash {Pinned(server)}).");
    }

    internal static string Name(LanguageServer server) => server.Spec.Name.Length > 0 ? server.Spec.Name : server.Id;

    /// <summary>The Java and Kotlin provider to use now: a server that replaces others once it has a grant, else <c>standard</c> (the servers without terms).</summary>
    internal string Provider() => Gated.FirstOrDefault(server => server.Spec.Replaces.Count > 0 && GrantFor(server) is not null)?.Id ?? JavaKotlinStandard;

    internal const string JavaKotlinStandard = "standard";

    /// <summary>
    /// The server with terms and no grant that a tool call needs, or null. A file type that another server without terms also serves
    /// (Kotlin) stays with that server until the terms are accepted; a type only the server with terms serves (Java) needs the terms.
    /// </summary>
    internal LanguageServer? NeededBy(JsonObject arguments) => Pending(arguments, shared: false);

    /// <summary>The server with terms and no grant that would serve a file now served by another server (Kotlin), or null; the answer says why it is not in use.</summary>
    internal LanguageServer? WaitingFor(JsonObject arguments) => Pending(arguments, shared: true);

    internal string NotInUse(LanguageServer server) =>
        $"{LanguageConsent.Name(server)} is not in use: its terms are not accepted, so the previous server answers. Once the user agrees (call accept_terms with the hash from a Java call's answer, or read the terms with accept_terms and only the server), {LanguageConsent.Name(server)} takes over Java and Kotlin automatically.";

    private LanguageServer? Pending(JsonObject arguments, bool shared)
    {
        var gated = Gated;
        if (gated.Count == 0) return null;
        var all = catalog();
        var names = new List<string>();
        if (arguments["path"] is JsonValue path && path.TryGetValue<string>(out var one)) names.Add(one);
        if (arguments["paths"] is JsonArray many) names.AddRange(many.OfType<JsonValue>().Select(item => item.TryGetValue<string>(out var text) ? text : ""));
        foreach (var name in names)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length == 0) continue;
            foreach (var server in gated)
                if (server.Extensions.Contains(extension) && all.Any(other => other != server && other.Spec.TermsAsync is null && other.Extensions.Contains(extension)) == shared && GrantFor(server) is null)
                    return server;
        }
        if (!shared && arguments["server"] is JsonValue id && id.TryGetValue<string>(out var wanted) && arguments["operation"]?.GetValue<string>() is "retry" or "restart")
            return gated.FirstOrDefault(server => server.Id == wanted && GrantFor(server) is null);
        return null;
    }

    private static string Choices(IReadOnlyList<string> values, IReadOnlyList<string> labels) =>
        string.Join(", ", values.Select((value, index) => index < labels.Count && labels[index] != value ? $"{value} ({labels[index]})" : value));

    private string Header(LanguageServer server) =>
        $"{Name(server)} {server.Spec.Version}. {server.Spec.TermsSummary}\n" +
        $"Terms: {server.Spec.TermsTitle}" + (server.Spec.TermsUrl.Length > 0 ? $", see {server.Spec.TermsUrl}" : "") + $".\nTerms hash (SHA-256 of the terms file in the download): {Pinned(server)}";

    private const int PageSize = 12_000;
    private const int WholeLimit = 24_000;

    // The download and the terms it carries. A download only fetches the pinned, checksummed archive; nothing in it runs before the user agreed.
    internal enum PrefetchState { None, Downloading, Ready, Failed }

    internal sealed record PrefetchInfo(PrefetchState State, string Detail, string Text);

    private sealed record Fetch(PrefetchState State, string Detail, string Text, DateTime At, Task Task);

    private readonly Dictionary<string, Fetch> fetches = new(StringComparer.Ordinal);

    internal PrefetchInfo Prefetched(LanguageServer server)
    {
        lock (gate) return fetches.TryGetValue(server.Id, out var fetch) ? new(fetch.State, fetch.Detail, fetch.Text) : new(PrefetchState.None, "", "");
    }

    /// <summary>
    /// Downloads the server's pinned archive in the background (nothing is executed from it) and reads the terms text it carries; the task ends when that is done.
    /// A running or finished fetch is reused; one that failed is tried again after half a minute.
    /// </summary>
    internal Task StartPrefetch(LanguageServer server)
    {
        lock (gate)
        {
            if (fetches.TryGetValue(server.Id, out var known) && (known.State != PrefetchState.Failed || DateTime.UtcNow - known.At < TimeSpan.FromSeconds(30))) return known.Task;
            var task = Task.Run(async () =>
            {
                try
                {
                    if (reader is null) throw new IOException("This host cannot read the server's terms.");
                    var terms = await reader(server, true, CancellationToken.None);
                    Set(server, !string.Equals(terms.TextSha256, Pinned(server), StringComparison.OrdinalIgnoreCase)
                        ? new(PrefetchState.Failed, $"The terms file in the download (hash {terms.TextSha256}) is not the pinned text ({Pinned(server)}). Nothing was started or accepted.", "", DateTime.UtcNow, Task.CompletedTask)
                        : new(PrefetchState.Ready, "", terms.Text, DateTime.UtcNow, Task.CompletedTask));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Set(server, new(PrefetchState.Failed, "The download failed: " + error.Message, "", DateTime.UtcNow, Task.CompletedTask));
                }
            });
            fetches[server.Id] = new(PrefetchState.Downloading, $"Downloading since {DateTime.UtcNow:HH:mm:ss} UTC.", "", DateTime.UtcNow, task);
            return task;
        }
    }

    private void Set(LanguageServer server, Fetch fetch)
    {
        lock (gate) fetches[server.Id] = fetch with { Task = fetches[server.Id].Task };
    }

    /// <summary>The servers named by <see cref="PrefetchVariable"/> that have terms, and warnings for the names that do not.</summary>
    internal (IReadOnlyList<LanguageServer> Servers, IReadOnlyList<string> Warnings) PrefetchRequests()
    {
        var servers = new List<LanguageServer>();
        var warnings = new List<string>();
        foreach (var id in (environment(PrefetchVariable) ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (Find(id) is { } server) servers.Add(server);
            else warnings.Add($"{PrefetchVariable} names {id}, which has no terms to review (use the warm command for other servers).");
        return (servers, warnings);
    }

    /// <summary>One page of a long text with a line saying where the others are.</summary>
    internal static string Page(string text, int page, string how)
    {
        var pages = Math.Max(1, (text.Length + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 1, pages);
        var slice = text.Substring((page - 1) * PageSize, Math.Min(PageSize, text.Length - (page - 1) * PageSize));
        return pages == 1 ? slice : slice + $"\n\n[page {page} of {pages}" + (page < pages ? $"; {how} for page {page + 1}" : "") + "]";
    }

    private string Questions(LanguageServer server)
    {
        var text = new StringBuilder();
        foreach (var question in server.Spec.TermsQuestions) text.Append($"- {question.Id}: {question.Prompt}. One of: {Choices(question.Values, question.Labels)}.\n");
        return text.ToString();
    }

    private string NextStep(LanguageServer server)
    {
        var text = new StringBuilder();
        text.Append("Next step: show the user the terms above (or tell them to read them), ask in chat whether they accept, and ask the questions below. ");
        text.Append("Only after the user clearly said yes, call the tool accept_terms with server \"").Append(server.Id).Append("\", hash \"").Append(Pinned(server)).Append("\" and answers. ");
        text.Append("Do not call it on your own, do not guess an answer and do not accept for the user. The tool only works with this exact hash.\n").Append(Questions(server));
        text.Append($"\nThe user can also start the host with {EnvironmentVariable}={server.Id}");
        foreach (var question in server.Spec.TermsQuestions) text.Append($", {AnswerVariable(server, question)}=<{question.Id}>");
        text.Append($" to accept without a question (use {server.Id}:<terms hash> to accept only this exact text).");
        if (server.Spec.Replaces.Count > 0) text.Append($"\nUntil the user agrees and {Name(server)} works (accepted, started, licensed), the other language servers keep serving what they serve (Kotlin keeps its Kotlin server); {Name(server)} is the only one for Java.");
        return text.ToString();
    }

    /// <summary>What the user is asked to agree to: the full terms text once the download has finished, else a summary and where things stand.</summary>
    private string Terms(LanguageServer server, int page, string how, bool whole)
    {
        var fetched = Prefetched(server);
        switch (fetched.State)
        {
            case PrefetchState.Ready:
                var text = fetched.Text.Length <= WholeLimit || !whole ? Page(fetched.Text, page, how) : Page(fetched.Text, 1, how);
                return $"{Header(server)}\n\nThe full terms text, from the file in the downloaded server:\n\n{text}";
            case PrefetchState.Failed:
                return $"{Header(server)}\n\n{fetched.Detail} It is tried again on the next call. The full terms text cannot be shown until a download has finished and matched the pinned hash.";
            default:
                return $"{Header(server)}\n\nThe server (about 380 MB) is being downloaded from the vendor and checked against a pinned SHA-256; nothing in it is executed. {fetched.Detail}\n" +
                    "The full terms text appears here once the download has finished: call again in a few minutes. Do not ask the user to accept before it has been shown.";
        }
    }

    /// <summary>The text a call gets while the terms are not accepted: what it is, the full terms once downloaded and the exact next step. Nothing was started.</summary>
    internal string Refusal(LanguageServer server, bool canPrompt = false)
    {
        var text = new StringBuilder();
        text.Append($"{Name(server)} needs the user's agreement to its terms before it can be used. Nothing was started.\n\n").Append(Terms(server, 1, "call accept_terms with server and page", true)).Append("\n\n");
        if (Prefetched(server).State == PrefetchState.Ready) text.Append(NextStep(server));
        if (canPrompt) text.Append("\nThe user declined or did not answer the prompt of this client.");
        return text.ToString();
    }

    /// <summary>The terms for reading (accept_terms with no hash), with the state, one page at a time.</summary>
    internal string Review(LanguageServer server, int page = 1)
    {
        var text = new StringBuilder();
        text.Append(GrantFor(server) is { } grant ? $"Accepted ({grant.Source}).\n\n" : "Not accepted.\n\n").Append(Terms(server, page, "call accept_terms with server and page", false)).Append('\n');
        if (Prefetched(server).State == PrefetchState.Ready && GrantFor(server) is null) text.Append('\n').Append(NextStep(server));
        return text.ToString();
    }

    /// <summary>The prompt for a client that supports elicitation: the terms and the questions, with the user's Accept or Decline. Only after the download finished.</summary>
    internal JsonObject ElicitationRequest(LanguageServer server)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var question in server.Spec.TermsQuestions)
        {
            properties[question.Id] = new JsonObject
            {
                ["type"] = "string",
                ["title"] = question.Prompt,
                ["enum"] = new JsonArray(question.Values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["enumNames"] = new JsonArray(question.Labels.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
            };
            required.Add((JsonNode?)JsonValue.Create(question.Id));
        }
        var terms = Prefetched(server).Text;
        // A client that cannot show a long message gets the first pages and where to read the rest.
        var shown = terms.Length <= WholeLimit ? terms : terms[..PageSize] + $"\n\n[the rest of the terms: ask the agent to call accept_terms with server \"{server.Id}\"]";
        return new JsonObject
        {
            ["message"] = $"An agent wants to use {Name(server)}. Accept its terms to allow it.\n\n{Header(server)}\n\n{shown}\n\nAccept only if you agree to these terms. Decline keeps {Name(server)} off.",
            ["requestedSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required }
        };
    }

    /// <summary>The answers of an elicitation reply, or null unless the user explicitly accepted and answered every question.</summary>
    internal static Dictionary<string, string>? ElicitationAnswers(LanguageServer server, JsonNode? reply)
    {
        if (reply is not JsonObject result || result["action"] is not JsonValue action || !action.TryGetValue<string>(out var name) || name != "accept") return null;
        var content = result["content"] as JsonObject;
        var answers = new Dictionary<string, string>();
        foreach (var question in server.Spec.TermsQuestions)
        {
            if (content?[question.Id] is not JsonValue value || !value.TryGetValue<string>(out var chosen) || !question.Values.Contains(chosen)) return null;
            answers[question.Id] = chosen;
        }
        return answers;
    }
}