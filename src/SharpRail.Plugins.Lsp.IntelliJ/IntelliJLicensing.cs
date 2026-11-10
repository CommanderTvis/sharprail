using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Lsp.IntelliJ;

/// <summary>
/// The server's own licensing protocol (<c>jetbrains/licensing/*</c>): the last pushed state per session, its projection for clients,
/// the hold on language answers while there is no license, and the actions that change the license. The server does all the licensing
/// work and owns the license store; this only carries it.
/// </summary>
internal static class IntelliJLicensing
{
    internal const string StateNotification = "jetbrains/licensing/state";
    private const string Prefix = "jetbrains/licensing/";
    internal const string DeclinedPage = "Failed to open the sign-in page";
    private static readonly string[] EnvironmentVariables = ["INTELLIJ_SERVER_LICENSE", "INTELLIJ_SERVER_LICENSE_FILE", "JETBRAINS_LICENSE_SERVER"];

    internal static readonly IReadOnlyList<string> ClientCapabilities = ["licensingUi"];

    /// <summary>How long each request waits; sign-in and license-server authorization outlast the server's own five minutes in the browser.</summary>
    internal static class Timeouts
    {
        internal static TimeSpan State = TimeSpan.FromSeconds(10);
        internal static TimeSpan Short = TimeSpan.FromSeconds(30);
        internal static TimeSpan Medium = TimeSpan.FromSeconds(60);
        internal static TimeSpan Browser = TimeSpan.FromMinutes(6);
    }

    private sealed class Tracker
    {
        internal readonly object Gate = new();
        internal JsonObject? State;
        internal int Pushes;
        internal int PageId;
        internal string? Page;
        internal string Busy = "";
        internal bool Discovered;
    }

    private static readonly ConditionalWeakTable<ILanguageServerSession, Tracker> Trackers = new();

    private static Tracker Of(ILanguageServerSession session) => Trackers.GetValue(session, _ => new());

    /// <summary>Whether the host starts the anonymous trial itself: <c>SHARPRAIL_INTELLIJ_AUTO_TRIAL=1</c>, which the headless host sets unless the user set it (<c>0</c> turns it off).</summary>
    internal static bool AutomaticTrial => Environment.GetEnvironmentVariable("SHARPRAIL_INTELLIJ_AUTO_TRIAL") == "1";

    internal static string? EnvironmentLicense() =>
        EnvironmentVariables.FirstOrDefault(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));

    internal static void OnMessage(ILanguageServerSession session, string method, JsonNode? parameters)
    {
        var tracker = Of(session);
        if (method == StateNotification && parameters is JsonObject state)
        {
            lock (tracker.Gate) { tracker.Pushes++; tracker.State = (JsonObject)state.DeepClone(); }
            Publish(session, tracker);
            if (state["activeLicense"] is null && !tracker.Discovered)
            {
                tracker.Discovered = true;
                _ = Task.Run(async () =>
                {
                    // A server holding no license looks for one in the environment, Toolbox or a product key file, once.
                    try { await session.RequestAsync(Prefix + "discovery/autoActivate", new JsonObject(), CancellationToken.None, Timeouts.Medium); }
                    catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException) { }
                    // A host with no window cannot show the trial button: it starts the free anonymous trial the accepted agreement allows, once, when nothing else licensed the server.
                    if (AutomaticTrial && EnvironmentLicense() is null)
                    {
                        bool unlicensed;
                        lock (tracker.Gate) unlicensed = tracker.State?["activeLicense"] is null;
                        if (unlicensed)
                            try { await ActionAsync(session, "trial", null, CancellationToken.None); }
                            catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException) { }
                    }
                });
            }
        }
        else if (method == "window/showDocument" && parameters?["external"] is JsonValue external && external.TryGetValue<bool>(out var isExternal) && isExternal && Text(parameters["uri"]) is { } uri)
        {
            lock (tracker.Gate) { tracker.PageId++; tracker.Page = uri; }
            Publish(session, tracker);
        }
    }

    private static void Publish(ILanguageServerSession session, Tracker tracker)
    {
        JsonObject view;
        lock (tracker.Gate)
        {
            if (tracker.State is null) return;
            view = Project(tracker.State);
            view["busy"] = tracker.Busy;
            view["environment"] = EnvironmentLicense();
            if (tracker.Page is not null) view["page"] = new JsonObject { ["id"] = tracker.PageId, ["uri"] = tracker.Page };
        }
        session.SetState(view.ToJsonString());
        var kind = view["kind"]!.GetValue<string>();
        session.SetUnavailable(kind is "parked" or "unlicensed" or "authenticating"
            ? view["message"]!.GetValue<string>() + " Open Settings, Code Intelligence, Java / Kotlin (IntelliJ) to activate a license." +
                (AutomaticTrial ? " Without a window, start the host with INTELLIJ_SERVER_LICENSE, INTELLIJ_SERVER_LICENSE_FILE or JETBRAINS_LICENSE_SERVER set to a license you own." : "")
            : null);
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static readonly Dictionary<string, string> SourceLabels = new()
    {
        ["trial"] = "Trial",
        ["eap"] = "Early Access Program",
        ["subscription"] = "JetBrains Account",
        ["activationCode"] = "Activation Code",
        ["licenseServer"] = "License Server",
        ["toolbox"] = "JetBrains Toolbox",
        ["productKeyFile"] = "Product Key File"
    };

    private static string ParkedMessage(string? reason) => reason switch
    {
        "GraceExpired" => "The grace period has ended. Activate a license to use Java and Kotlin features.",
        "LicenseRemoved" => "The license was removed. Activate another license to use Java and Kotlin features.",
        "LicenseRevoked" => "The license was revoked. Activate another license to use Java and Kotlin features.",
        _ => "Activate a license to use Java and Kotlin features."
    };

    /// <summary>What to show for a pushed state, with the same precedence and sentences as JetBrains' own clients.</summary>
    internal static JsonObject Project(JsonObject state)
    {
        var jba = state["jbaLicensingState"] as JsonObject ?? new();
        var server = state["licenseServerFlowState"] as JsonObject ?? new();
        var active = state["activeLicense"] as JsonObject;
        var valid = active?["status"]?.ToString() == "Valid";
        long? deadline = !valid && state["graceDeadline"] is JsonValue grace && grace.TryGetValue<long>(out var ms) ? ms : null;
        var configured = server["kind"]?.ToString() == "configured";
        var authentication = jba["authenticationStatus"]?.ToString();
        var ticket = configured ? Text(server["floatingTicketStatus"]) : null;
        var view = new JsonObject { ["serverAddress"] = configured ? Text(server["serverAddress"]) : null, ["ticket"] = ticket };
        if (authentication is "Authenticated" or "FailedToProlongateAuthentication" && jba["accountInfo"] is JsonObject info)
            view["account"] = new JsonObject { ["displayName"] = Text(info["displayName"]), ["email"] = Text(info["email"]) };
        var subscriptions = new JsonArray();
        foreach (var item in jba["availableSubscriptions"] as JsonArray ?? [])
            if (item is JsonObject sub)
                subscriptions.Add((JsonNode)new JsonObject
                {
                    ["assetId"] = Text(sub["assetId"]),
                    ["product"] = Text(sub["product"]),
                    ["licensedTo"] = Text(sub["licensedTo"]),
                    ["status"] = Text(sub["status"]),
                    ["validThrough"] = Text(sub["validThrough"])
                });
        view["subscriptions"] = subscriptions;
        var serverError = configured && (ticket == "FailedToRefresh" || Text(server["errorMessage"]) is not null)
            ? Text(server["errorMessage"])?.Trim() ?? "Lost connection to the license server and couldn't renew your license. Check the server and try again." : null;
        view["error"] = authentication == "FailedToAuthenticate" ? "Couldn't sign in to your JetBrains Account. Try again or use another activation method." : serverError;
        string? source = null;
        if (active is not null)
        {
            source = Text(active["source"]);
            view["source"] = source;
            view["sourceLabel"] = source is not null && SourceLabels.TryGetValue(source, out var label) ? label : source;
            view["status"] = Text(active["status"]);
            view["holder"] = source switch { "trial" => "Trial User", "licenseServer" => Text(active["serverAddress"]), _ => Text(active["licensedTo"]) };
            var through = Text(active["validThrough"]);
            view["validThrough"] = through;
            if (active["daysLeft"] is JsonValue days && days.TryGetValue<int>(out var left) && (through is not null || !valid || left > 0)) view["daysLeft"] = left;
        }
        if (deadline is not null) view["graceDeadline"] = deadline;
        string kind, message;
        if (!valid && authentication == "Authenticating") (kind, message) = ("authenticating", "Waiting for the JetBrains Account sign-in in your browser…");
        else if (!valid && configured && ticket is "Obtaining" or "Refreshing")
            (kind, message) = ("authenticating", "Getting a license from " + (view["serverAddress"]?.ToString() ?? "the license server") + "…");
        else if (valid && authentication == "FailedToProlongateAuthentication")
            (kind, message) = ("session-expired", "Your JetBrains Account session expired. Sign in again to keep your license active.");
        else if (valid) (kind, message) = ("licensed", "License is active (" + view["sourceLabel"] + ")");
        else if (deadline is not null)
            (kind, message) = ("grace", (active is null ? "No valid license" : "License " + view["status"]) + ". Features work until "
                + DateTimeOffset.FromUnixTimeMilliseconds(deadline.Value).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + ".");
        else if (state["featuresDeclined"] is JsonValue declined && declined.TryGetValue<bool>(out var parked) && parked)
        {
            var reason = Text(state["parkedReason"]);
            kind = "parked";
            message = (active is not null ? "License " + view["status"] + ". " : reason is null or "StartedUnlicensed" ? "No active license. " : "") + ParkedMessage(reason);
        }
        else (kind, message) = ("unlicensed", active is null ? "No active license." : "License " + view["status"] + ".");
        view["kind"] = kind;
        view["message"] = message;
        if (kind == "licensed" && source == "trial" && view["daysLeft"] is { } trialDays)
        {
            var count = trialDays.GetValue<int>();
            if (count <= 7)
                view["notice"] = "The trial ends " + (count <= 0 ? "today" : count == 1 ? "tomorrow" : $"in {count} days") + ". Activate a license to keep Java and Kotlin features.";
        }
        else if (kind is "grace" or "parked") view["notice"] = message;
        return view;
    }

    private static string Clean(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    internal static bool ValidServerUrl(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 && !url.Trim().Any(char.IsWhiteSpace);

    private static JsonObject Result(bool ok, string? message = null) => new() { ["ok"] = ok, ["message"] = message };

    internal static async Task<JsonNode?> ActionAsync(ILanguageServerSession session, string name, JsonNode? arguments, CancellationToken token)
    {
        var tracker = Of(session);
        string Argument(string key) => Text(arguments?[key]) ?? throw new ArgumentException($"The {name} action needs {key}.");
        if (name is "trial" or "signIn" or "useSubscription" or "code" or "serverAdd" && EnvironmentLicense() is { } variable)
            return Result(false, $"The license is set through {variable}, which overrides any license activated here. Unset it and restart the server to activate a license from SharpRail.");
        lock (tracker.Gate) tracker.Busy = name;
        Publish(session, tracker);
        try { return await RunAsync(session, tracker, name, Argument, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Signing out ends a sign-in the server still waits on in the browser.
            if (name == "signIn") await EndWaitAsync(session);
            throw;
        }
        finally
        {
            lock (tracker.Gate) { tracker.Busy = ""; tracker.Page = null; }
            Publish(session, tracker);
        }
    }

    private static async Task EndWaitAsync(ILanguageServerSession session)
    {
        try { await session.RequestAsync(Prefix + "jba/logout", new JsonObject(), CancellationToken.None, Timeouts.Short); }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException) { }
    }

    private static async Task<JsonNode?> RunAsync(ILanguageServerSession session, Tracker tracker, string name, Func<string, string> argument, CancellationToken token)
    {
        Task<JsonNode?> Send(string method, JsonObject? parameters, TimeSpan timeout) => session.RequestAsync(Prefix + method, parameters ?? new JsonObject(), token, timeout);
        string? Authenticated() { lock (tracker.Gate) return tracker.State?["jbaLicensingState"]?["authenticationStatus"]?.ToString(); }
        try
        {
            switch (name)
            {
                case "refresh":
                    int before; lock (tracker.Gate) before = tracker.Pushes;
                    var fetched = await Send("state/get", null, Timeouts.State);
                    // A push that arrived while the request was in flight is newer than its reply.
                    if (fetched is JsonObject current) { lock (tracker.Gate) if (tracker.Pushes == before) tracker.State = (JsonObject)current.DeepClone(); }
                    return Result(fetched is JsonObject);
                case "trial":
                    if (await Send("trial/startAnonymous", null, Timeouts.Medium) is not JsonObject trial || trial["isSuccess"] is not JsonValue success || !success.TryGetValue<bool>(out var started) || !started)
                        return Result(false, "Couldn't start your trial. Try again.");
                    if (Authenticated() == "Authenticated") await Send("jba/logout", null, Timeouts.Short);
                    return Result(true);
                case "signIn":
                    return await SignInAsync(tracker, Send);
                case "useSubscription":
                    var asset = argument("assetId");
                    await Send("subscriptions/use", new JsonObject { ["assetId"] = asset }, Timeouts.Short);
                    return Active(tracker) is { } chosen && chosen["source"]?.ToString() == "subscription" && chosen["status"]?.ToString() == "Valid" && chosen["assetId"]?.ToString() == asset
                        ? Result(true) : Result(false, "Couldn't activate a subscription from this JetBrains Account. Try again.");
                case "cancelSignIn":
                case "signOut":
                    await Send("jba/logout", null, Timeouts.Short);
                    return Result(true);
                case "code":
                    var code = Clean(argument("activationCode"));
                    if (await Send("activationCode/use", new JsonObject { ["activationCode"] = code }, Timeouts.Medium) is not JsonValue accepted || !accepted.TryGetValue<bool>(out var ok) || !ok)
                        return Result(false, "That activation code isn't valid. Check the code and try again.");
                    if (Authenticated() == "Authenticated") await Send("jba/logout", null, Timeouts.Short);
                    return Result(true);
                case "serverAdd":
                    var address = argument("serverAddress").Trim();
                    if (!ValidServerUrl(address)) return Result(false, "A license server URL starts with http:// or https://.");
                    return ServerOutcome(await Send("licenseServer/add", new JsonObject { ["serverAddress"] = address }, Timeouts.Browser));
                case "serverReconnect":
                    return ServerOutcome(await Send("licenseServer/reconnect", null, Timeouts.Browser));
                case "serverRemove":
                    await Send("licenseServer/remove", null, Timeouts.Short);
                    return Result(true);
                case "removeLicense":
                    switch (Active(tracker)?["source"]?.ToString())
                    {
                        case "subscription": await Send("subscriptions/unuseAll", null, Timeouts.Short); await Send("jba/logout", null, Timeouts.Short); break;
                        case "activationCode": await Send("activationCode/disable", null, Timeouts.Medium); break;
                        case "licenseServer": await Send("licenseServer/remove", null, Timeouts.Short); break;
                        default: return Result(false, "This license cannot be removed from here.");
                    }
                    return Result(true);
                default:
                    throw new ArgumentException("Unknown license action: " + name);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Result(false, "The server did not answer in time. Try again.");
        }
    }

    private static JsonObject? Active(Tracker tracker) { lock (tracker.Gate) return tracker.State?["activeLicense"] as JsonObject; }

    private static JsonObject ServerOutcome(JsonNode? reply)
    {
        if (reply is not JsonObject outcome) return Result(false, "Couldn't activate with the license server. Check the server and try again.");
        var message = Text(outcome["errorMessage"])?.Trim();
        var status = outcome["status"]?.ToString();
        if (status == "Connected") return Result(true);
        if (status == "Canceled" || message == DeclinedPage) return Result(false);
        if (status == "TimedOut") return Result(false, "Sign-in to the license server timed out. Try again.");
        return Result(false, message ?? "Couldn't activate with the license server. Check the server and try again.");
    }

    private static async Task<JsonNode?> SignInAsync(Tracker tracker, Func<string, JsonObject?, TimeSpan, Task<JsonNode?>> send)
    {
        var reply = await send("jba/login", null, Timeouts.Browser);
        var status = reply?["status"]?.ToString();
        var message = Text(reply?["errorMessage"])?.Trim();
        switch (status)
        {
            case "Licensed": return Result(true);
            case "NoSuitableLicense":
                List<JsonObject> valid;
                lock (tracker.Gate) valid = (tracker.State?["jbaLicensingState"]?["availableSubscriptions"] as JsonArray ?? []).OfType<JsonObject>().Where(item => item["status"]?.ToString() == "Valid").ToList();
                if (valid.Count == 0)
                {
                    await send("jba/logout", null, Timeouts.Short);
                    return Result(false, "No licenses available for activation were found in the JetBrains Account.");
                }
                if (valid.Count > 1) return Result(false, "Signed in. Choose which subscription to use.");
                await send("subscriptions/use", new JsonObject { ["assetId"] = valid[0]["assetId"]?.ToString() }, Timeouts.Short);
                return Active(tracker) is { } active && active["status"]?.ToString() == "Valid" ? Result(true) : Result(false, "Couldn't activate a subscription from this JetBrains Account. Try again.");
            case "NoLicenses": return Result(false, "No licenses available for activation were found in the JetBrains Account.");
            case "LicenseCheckFailed": return Result(false, "Couldn't check licenses in the JetBrains Account. Try again.");
            case "Canceled": return Result(false);
            default:
                if (status == "Failed" && message == DeclinedPage) return Result(false);
                return Result(false, "Couldn't sign in to your JetBrains Account. Try again or use another activation method.");
        }
    }
}