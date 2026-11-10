using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.Lsp.IntelliJ;

namespace SharpRail.Checks;

/// <summary>A fake licensing server (an in-process LSP peer) behind the IntelliJ plugin's license state and actions.</summary>
internal static class IntelliJLicenseChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    /// <summary>Answers <c>jetbrains/licensing/*</c> and pushes whole states before its replies, as the server does.</summary>
    private sealed class FakeServer : ILanguageServerSession
    {
        internal JsonObject State = new() { ["featuresDeclined"] = true, ["parkedReason"] = "StartedUnlicensed" };
        internal readonly List<string> Requests = [];
        internal string? Published;
        internal string? Unavailable;
        internal Func<string, JsonNode?, CancellationToken, Task<JsonNode?>>? Handler;

        public string Workspace => "/fake";
        public bool SupportsCommand(string command) => false;
        public void SetProgress(string key, string? message) { }
        public void Track(string key, string describe, Func<CancellationToken, Task> work) { }
        public bool Contains(string path) => false;
        public void SetState(string? state) => Published = state;
        public void SetUnavailable(string? reason) => Unavailable = reason;

        internal void Push(JsonObject state)
        {
            State = state;
            IntelliJLicensing.OnMessage(this, IntelliJLicensing.StateNotification, state.DeepClone());
        }

        public async Task<JsonNode?> RequestAsync(string method, object? parameters, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            Requests.Add(method + (parameters is null ? "" : " " + System.Text.Json.JsonSerializer.Serialize(parameters)));
            var node = parameters is null ? null : JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(parameters));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
            return await Handler!(method["jetbrains/licensing/".Length..], node, deadline.Token);
        }
    }

    private static JsonObject Licensed(string source, int? days = null, string status = "Valid") => new()
    {
        ["activeLicense"] = new JsonObject { ["source"] = source, ["status"] = status, ["product"] = "intellij-server", ["licensedTo"] = "Ada", ["daysLeft"] = days, ["validThrough"] = "2026-12-02", ["assetId"] = "asset-1" }
    };

    private static JsonObject View(FakeServer server) => (JsonObject)JsonNode.Parse(server.Published!)!;

    private static async Task<JsonObject> Run(FakeServer server, string name, JsonObject? arguments = null, CancellationToken token = default) =>
        (JsonObject)(await IntelliJLicensing.ActionAsync(server, name, arguments, token))!;

    internal static async Task Run()
    {
        var saved = IntelliJLicensing.EnvironmentLicense();
        Require(saved is null, "The check needs an environment without an environment license; unset " + saved + ".");
        var server = new FakeServer();
        server.Handler = (method, arguments, token) => Task.FromResult<JsonNode?>(method switch
        {
            "discovery/autoActivate" => new JsonObject { ["activated"] = false },
            "state/get" => server.State.DeepClone(),
            "trial/startAnonymous" => Start(),
            "activationCode/use" => Code(arguments),
            "jba/logout" => Logout(),
            "licenseServer/add" => AddServer(arguments),
            "licenseServer/reconnect" => new JsonObject { ["status"] = "Connected" },
            "licenseServer/remove" => Removed(),
            "activationCode/disable" => Removed(),
            "subscriptions/unuseAll" => Removed(),
            "subscriptions/use" => Use(arguments),
            _ => throw new IOException("Unexpected request " + method)
        });
        JsonNode? Start() { server.Push(Licensed("trial", 30)); return new JsonObject { ["isSuccess"] = true }; }
        JsonNode? Code(JsonNode? arguments) { if (arguments?["activationCode"]?.ToString() != "ABC-123") return false; server.Push(Licensed("activationCode", 300)); return true; }
        JsonNode? Logout() { var next = (JsonObject)server.State.DeepClone(); next["jbaLicensingState"] = new JsonObject { ["authenticationStatus"] = "NotAuthenticated" }; server.Push(next); return null; }
        JsonNode? AddServer(JsonNode? arguments) { server.Push(new JsonObject { ["activeLicense"] = new JsonObject { ["source"] = "licenseServer", ["status"] = "Valid", ["product"] = "intellij-server", ["serverAddress"] = arguments!["serverAddress"]!.ToString() }, ["licenseServerFlowState"] = new JsonObject { ["kind"] = "configured", ["serverAddress"] = arguments["serverAddress"]!.ToString(), ["floatingTicketStatus"] = "Obtained" } }); return new JsonObject { ["status"] = "Connected" }; }
        JsonNode? Removed() { server.Push(new JsonObject { ["featuresDeclined"] = true, ["parkedReason"] = "LicenseRemoved" }); return null; }
        JsonNode? Use(JsonNode? arguments) { var next = Licensed("subscription", 200); next["activeLicense"]!["assetId"] = arguments!["assetId"]!.ToString(); server.Push(next); return null; }

        // Unlicensed: the state is surfaced, and the session answers with the reason instead of silence.
        server.Push(server.State);
        Require(View(server)["kind"]!.ToString() == "parked" && server.Unavailable is { } reason && reason.Contains("No active license", StringComparison.Ordinal), "An unlicensed server was not held back with a reason.");
        await Task.Delay(100);
        Require(server.Requests.Contains("jetbrains/licensing/discovery/autoActivate {}"), "A server without a license was not offered license discovery.");

        // Trial start, then the days left and the end-of-trial notice.
        var started = await Run(server, "trial");
        Require(started["ok"]!.GetValue<bool>() && View(server)["kind"]!.ToString() == "licensed" && View(server)["daysLeft"]!.GetValue<int>() == 30 && server.Unavailable is null && View(server)["notice"] is null, "A started trial did not release the hold.");
        server.Push(Licensed("trial", 3));
        Require(View(server)["notice"]!.ToString().Contains("3 days", StringComparison.Ordinal), "A trial ending soon showed no notice.");
        server.Push(Licensed("trial", 0));
        Require(View(server)["notice"]!.ToString().Contains("today", StringComparison.Ordinal), "The last trial day showed no notice.");

        // Grace keeps features but warns; an expired license without grace is unlicensed and holds answers back.
        server.Push(new JsonObject { ["activeLicense"] = Licensed("subscription", 0, "Expired")["activeLicense"]!.DeepClone(), ["graceDeadline"] = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds() });
        Require(View(server)["kind"]!.ToString() == "grace" && server.Unavailable is null && View(server)["notice"] is not null, "Grace blocked features or hid its notice.");
        server.Push(new JsonObject { ["activeLicense"] = Licensed("subscription", 0, "Expired")["activeLicense"]!.DeepClone() });
        Require(View(server)["kind"]!.ToString() == "unlicensed" && server.Unavailable is not null, "An expired license without grace was not held back.");
        server.Push(new JsonObject { ["activeLicense"] = Licensed("subscription", 40)["activeLicense"]!.DeepClone(), ["graceDeadline"] = 1L });
        Require(View(server)["kind"]!.ToString() == "licensed" && View(server)["graceDeadline"] is null, "A valid license kept a contradictory grace deadline.");

        // Activation code: whitespace is removed, a rejected code is reported, an accepted one licenses.
        server.Push(new JsonObject { ["featuresDeclined"] = true });
        var rejected = await Run(server, "code", new() { ["activationCode"] = "nope" });
        Require(!rejected["ok"]!.GetValue<bool>() && rejected["message"]!.ToString().Contains("isn't valid", StringComparison.Ordinal), "A rejected code was not reported.");
        var accepted = await Run(server, "code", new() { ["activationCode"] = " ABC -\n123 " });
        Require(accepted["ok"]!.GetValue<bool>() && View(server)["source"]!.ToString() == "activationCode", "An activation code did not license.");
        var removed = await Run(server, "removeLicense");
        Require(removed["ok"]!.GetValue<bool>() && server.Requests.Last().StartsWith("jetbrains/licensing/activationCode/disable", StringComparison.Ordinal) && View(server)["kind"]!.ToString() == "parked", "Removing an activation-code license used the wrong request.");

        // License server: URL validation, connect, reconnect, remove.
        Require(!(await Run(server, "serverAdd", new() { ["serverAddress"] = "ftp://x" }))["ok"]!.GetValue<bool>(), "A non-http license server address was accepted.");
        Require((await Run(server, "serverAdd", new() { ["serverAddress"] = "https://licenses.example.com" }))["ok"]!.GetValue<bool>() && View(server)["serverAddress"]!.ToString() == "https://licenses.example.com", "A license server did not connect.");
        Require((await Run(server, "serverReconnect"))["ok"]!.GetValue<bool>(), "Reconnect failed.");
        Require((await Run(server, "removeLicense"))["ok"]!.GetValue<bool>() && server.Requests.Last().Contains("licenseServer/remove", StringComparison.Ordinal), "Removing a license-server license used the wrong request.");

        // JetBrains Account: the page is published for the client to open, a single valid subscription is used, several are offered.
        var subscription = (string id, string status) => new JsonObject { ["assetId"] = id, ["product"] = "intellij-server", ["licensedTo"] = "Ada", ["status"] = status, ["validThrough"] = "2027-01-01" };
        var login = new TaskCompletionSource<JsonNode?>();
        server.Handler = (method, arguments, token) => method switch
        {
            "jba/login" => login.Task.WaitAsync(token),
            "jba/logout" => Task.FromResult<JsonNode?>(Logout()),
            "subscriptions/use" => Task.FromResult<JsonNode?>(Use(arguments)),
            _ => Task.FromResult<JsonNode?>(null)
        };
        var signingIn = Run(server, "signIn");
        await Task.Delay(50);
        server.Push(new JsonObject { ["featuresDeclined"] = true, ["jbaLicensingState"] = new JsonObject { ["authenticationStatus"] = "Authenticating" } });
        IntelliJLicensing.OnMessage(server, "window/showDocument", new JsonObject { ["uri"] = "https://account.jetbrains.com/sign-in", ["external"] = true });
        Require(View(server)["busy"]!.ToString() == "signIn" && View(server)["page"]!["uri"]!.ToString().StartsWith("https://account.jetbrains.com", StringComparison.Ordinal) && View(server)["kind"]!.ToString() == "authenticating", "The sign-in page and waiting state were not surfaced.");
        server.Push(new JsonObject { ["featuresDeclined"] = true, ["jbaLicensingState"] = new JsonObject { ["authenticationStatus"] = "Authenticated", ["accountInfo"] = new JsonObject { ["displayName"] = "Ada" }, ["availableSubscriptions"] = new JsonArray(subscription("asset-1", "Valid"), subscription("asset-2", "Expired")) } });
        login.SetResult(new JsonObject { ["status"] = "NoSuitableLicense" });
        var signedIn = await signingIn;
        Require(signedIn["ok"]!.GetValue<bool>() && View(server)["kind"]!.ToString() == "licensed" && View(server)["busy"]!.ToString() == "" && View(server)["page"] is null, "A sign-in with one valid subscription did not license.");
        server.Push(new JsonObject { ["featuresDeclined"] = true, ["jbaLicensingState"] = new JsonObject { ["authenticationStatus"] = "Authenticated", ["availableSubscriptions"] = new JsonArray(subscription("asset-1", "Valid"), subscription("asset-3", "Valid")) } });
        login = new();
        login.SetResult(new JsonObject { ["status"] = "NoSuitableLicense" });
        var several = await Run(server, "signIn");
        Require(!several["ok"]!.GetValue<bool>() && View(server)["subscriptions"]!.AsArray().Count == 2, "Several subscriptions were not offered.");
        Require((await Run(server, "useSubscription", new() { ["assetId"] = "asset-3" }))["ok"]!.GetValue<bool>(), "A chosen subscription did not activate.");

        // Cancelling a sign-in stops waiting and signs out so the server ends its wait in the browser.
        server.Push(new JsonObject { ["featuresDeclined"] = true });
        login = new();
        using (var cancel = new CancellationTokenSource())
        {
            var pending = Run(server, "signIn", token: cancel.Token);
            await Task.Delay(50);
            var before = server.Requests.Count;
            cancel.Cancel();
            try { await pending; throw new InvalidOperationException("A cancelled sign-in completed."); }
            catch (OperationCanceledException) { }
            Require(server.Requests.Skip(before).Any(request => request.StartsWith("jetbrains/licensing/jba/logout", StringComparison.Ordinal)) && View(server)["busy"]!.ToString() == "", "Cancelling a sign-in did not end the server's wait.");
        }

        // A request the server never answers times out with a message instead of hanging; the sign-in waits longer than the server's five minutes.
        Require(IntelliJLicensing.Timeouts.Browser > TimeSpan.FromMinutes(5), "The sign-in must outlast the server's five-minute wait.");
        var shortened = IntelliJLicensing.Timeouts.Medium;
        IntelliJLicensing.Timeouts.Medium = TimeSpan.FromMilliseconds(100);
        try
        {
            server.Handler = async (method, arguments, token) => { await Task.Delay(Timeout.Infinite, token); return null; };
            var late = await Run(server, "trial");
            Require(!late["ok"]!.GetValue<bool>() && late["message"]!.ToString().Contains("did not answer", StringComparison.Ordinal), "A silent server was not reported.");
        }
        finally { IntelliJLicensing.Timeouts.Medium = shortened; }

        // A license set in the environment overrides activation from here.
        Environment.SetEnvironmentVariable("INTELLIJ_SERVER_LICENSE", "fixture");
        try
        {
            var blocked = await Run(server, "trial");
            Require(!blocked["ok"]!.GetValue<bool>() && blocked["message"]!.ToString().Contains("INTELLIJ_SERVER_LICENSE", StringComparison.Ordinal), "An environment license was not explained.");
        }
        finally { Environment.SetEnvironmentVariable("INTELLIJ_SERVER_LICENSE", null); }

        try { await Run(server, "nonsense"); throw new InvalidOperationException("An unknown license action ran."); }
        catch (ArgumentException) { }
        Console.WriteLine("PASS IntelliJ license state, hold, trial/sign-in/code/license-server/remove actions, cancellation, timeout and environment license against a fake licensing server");
    }

    /// <summary>The settings block: state, days left, the trial-ending notice, the offered actions and the call a click makes.</summary>
    internal static void Ui(string fixture)
    {
        using var app = new E2E.E2eWorkspace(Path.Combine(fixture, "intellij-license-ui"), openFiles: false);
        E2E.E2eWorkspace.Until(() => app.State!.Current.Workspaces.Any(item => item.Path == app.Root));
        var calls = new List<LanguageRequest>();
        string? rowState = null;
        app.Host.LanguageReply = request =>
        {
            if (request.Operation == "servers" && rowState is not null)
                return new("ready", "") { Servers = [new("intellij", "", "ready", "Ready", "263.7084.0") { PluginState = rowState, Presentation = new("Java / Kotlin (IntelliJ)") }] };
            if (request.Operation != "action") return null;
            calls.Add(request);
            return new("ready", """{"ok":true,"message":null}""");
        };
        var trial = Licensing(new JsonObject { ["activeLicense"] = new JsonObject { ["source"] = "trial", ["status"] = "Valid", ["product"] = "intellij-server", ["daysLeft"] = 3, ["validThrough"] = "2026-11-05" } });
        var view = new SharpRail.UI.Panels.LanguageConfigurationView(app.Host, app.Root, new("intellij", "", "ready", "Ready", "263.7084.0") { PluginState = trial });
        var window = new Avalonia.Controls.Window { Width = 700, Height = 900, Content = view };
        window.Show(app.Window);
        try
        {
            T Find<T>(string name) where T : Avalonia.Controls.Control => Avalonia.Controls.ControlExtensions.FindControl<T>(view, name)!;
            Require(Find<Avalonia.Controls.StackPanel>("LicensePanel").IsVisible, "The license block was hidden for a licensed IntelliJ.");
            Require(Find<Avalonia.Controls.TextBlock>("LicenseState").Text!.Contains("3 days left (valid through 2026-11-05)", StringComparison.Ordinal), "Trial days left were not shown: " + Find<Avalonia.Controls.TextBlock>("LicenseState").Text);
            Require(Find<Avalonia.Controls.TextBlock>("LicenseNotice") is { IsVisible: true } notice && notice.Text!.Contains("3 days", StringComparison.Ordinal), "A trial ending soon showed no notice.");
            view.Update(new("intellij", "", "actionRequired", "No active license.", "263.7084.0") { PluginState = Licensing(new JsonObject { ["featuresDeclined"] = true }) });
            Require(Find<Avalonia.Controls.TextBlock>("LicenseState").Text!.Contains("No active license", StringComparison.Ordinal) && Find<Avalonia.Controls.TextBlock>("LicenseNotice").IsVisible, "An unlicensed server did not say so.");
            Require(!Find<Avalonia.Controls.Button>("RemoveLicense").IsVisible && Find<Avalonia.Controls.Button>("StartTrial").IsEnabled && Find<Avalonia.Controls.Button>("SignIn").IsEnabled, "Unlicensed actions were not offered.");
            Find<Avalonia.Controls.Button>("StartTrial").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => calls.Count > 0 && Find<Avalonia.Controls.TextBlock>("ConfigurationMessage").Text == "License updated.");
            Require(calls[0].Server == "intellij" && calls[0].Query == "trial", "Start trial did not request the trial action.");
            Find<Avalonia.Controls.TextBox>("ActivationCode").Text = " AB-12 ";
            Find<Avalonia.Controls.Button>("UseCode").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => calls.Count > 1);
            Require(calls[1].Query == "code" && JsonNode.Parse(calls[1].Configuration!)!["activationCode"]!.ToString() == " AB-12 ", "The activation code was not sent.");
            // The Code Intelligence row itself shows the notice, without opening the settings.
            rowState = trial;
            var pane = new SharpRail.UI.Panels.LanguagePanel(app.Host, app.Root, _ => Task.CompletedTask, app.Workbench.State);
            var paneWindow = new Avalonia.Controls.Window { Width = 520, Height = 700, Content = pane };
            paneWindow.Show();
            try
            {
                E2E.E2eWorkspace.Until(() =>
                {
                    if (pane.Idle) pane.RefreshNow();
                    return Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(pane).OfType<Avalonia.Controls.TextBlock>()
                        .Any(text => text.Name == "ServerNotice" && text.IsEffectivelyVisible && (text.Text ?? "").Contains("3 days", StringComparison.Ordinal));
                }, "The Code Intelligence row did not show the trial notice.");
            }
            finally { paneWindow.Close(); }
            var plain = new SharpRail.UI.Panels.LanguageConfigurationView(app.Host, app.Root, new("typescript", "", "idle", "Fixture", "fixture"));
            Require(!Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.StackPanel>(plain, "LicensePanel")!.IsVisible, "A server without license state showed a license block.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS IntelliJ settings license block: state, trial days and notice, unlicensed message, actions and their host calls");
    }

    /// <summary>The terms status is three-state: unknown shows no button, only a known state offers review, and the last known acceptance is cached per server version.</summary>
    internal static void TermsStatus()
    {
        var checking = SharpRail.UI.Panels.TermsStatusView.Of(SharpRail.UI.Panels.TermsKnowledge.Checking);
        Require(!checking.ShowReview && !checking.ReviewEnabled && !checking.Text.Contains("not accepted", StringComparison.OrdinalIgnoreCase), "The unknown state must show no button and not claim the terms are not accepted: " + checking.Text);
        var notAccepted = SharpRail.UI.Panels.TermsStatusView.Of(SharpRail.UI.Panels.TermsKnowledge.NotAccepted);
        Require(notAccepted.ShowReview && notAccepted.ReviewEnabled && notAccepted.Text.StartsWith("Terms not accepted", StringComparison.Ordinal), "A known not-accepted state must offer Review and accept terms.");
        var accepted = SharpRail.UI.Panels.TermsStatusView.Of(SharpRail.UI.Panels.TermsKnowledge.Accepted, "2026-11-07", "263.7084.0");
        Require(accepted.Text == "Terms accepted on 2026-11-07 (version 263.7084.0)." && !accepted.ReviewEnabled, "The accepted state must show the date and version: " + accepted.Text);
        const string server = "terms-cache-fixture";
        Require(!SharpRail.UI.Panels.TermsAcceptanceCache.TryGet(server, "1", out _, out _), "An empty cache returned an acceptance.");
        SharpRail.UI.Panels.TermsAcceptanceCache.Store(server, "1", "2026-11-07", "263.7084.0");
        Require(SharpRail.UI.Panels.TermsAcceptanceCache.TryGet(server, "1", out var at, out var version) && at == "2026-11-07" && version == "263.7084.0", "The cache did not return the stored acceptance.");
        Require(!SharpRail.UI.Panels.TermsAcceptanceCache.TryGet(server, "2", out _, out _), "A new server version must invalidate the cached acceptance.");
        Require(!SharpRail.UI.Panels.TermsAcceptanceCache.TryGet(server, "1", out _, out _), "The invalidated acceptance came back.");
        SharpRail.UI.Panels.TermsAcceptanceCache.Store(server, "1", "2026-11-07", "");
        SharpRail.UI.Panels.TermsAcceptanceCache.Forget(server);
        Require(!SharpRail.UI.Panels.TermsAcceptanceCache.TryGet(server, "1", out _, out _), "Forget kept the acceptance.");
        Console.WriteLine("PASS Settings terms status: unknown shows no button, known states offer review, acceptance cache invalidates on a new server version");
    }

    /// <summary>Settings › Code Intelligence: a server with terms shows their status, a review button, and Install now once accepted; one without shows none of it.</summary>
    internal static void SettingsTerms(string fixture)
    {
        using var app = new E2E.E2eWorkspace(Path.Combine(fixture, "settings-terms"), openFiles: false);
        var terms = new List<LanguageTermsRequest>();
        var provisions = new List<LanguageRequest>();
        var accepted = false;
        var installed = false;
        var failInstall = true;
        const string Hash = "0123456789012345678901234567890123456789012345678901234567890123";
        app.Host.TermsReply = request =>
        {
            terms.Add(request);
            if (request.Accept) accepted = true;
            return new(accepted ? "accepted" : "requiresAcceptance", "Fixture", "263.7084.0", Hash, Hash, "Fixture terms", accepted, accepted ? "2026-11-07T10:00:00.0000000+00:00" : "");
        };
        app.Host.LanguageReply = request =>
        {
            if (request.Operation == "catalog")
                return new("ready", "storage")
                {
                    Servers =
                    [
                        new("intellij", "", "available", "IntelliJ Java and Kotlin · JetBrains", "263.7084.0") { Presentation = new("Java / Kotlin (IntelliJ)", HasTerms: true) },
                        new("typescript", "", "available", "TypeScript", "5.0") { Presentation = new("TypeScript") }
                    ]
                };
            if (request.Operation == "resolution")
                return new(installed ? "ready" : "unavailable", installed ? "Ready" : "Not installed yet", request.Server)
                { Configuration = new JsonObject { ["path"] = installed ? "/fixture/idea" : "", ["version"] = "", ["warning"] = "", ["source"] = installed ? "managed" : "none", ["pathCapable"] = false, ["pinning"] = "" }.ToJsonString() };
            if (request.Operation == "provision")
            {
                provisions.Add(request);
                if (failInstall) return new("error", "Fixture download failed", request.Server);
                installed = true;
                return new("ready", "Installed.", request.Server);
            }
            return null;
        };
        var settings = new SharpRail.UI.Panels.SettingsView(app.Window, () => { }, section: "Code Intelligence");
        app.Window.ShowInOverlay(settings);
        try
        {
            T Find<T>(string name) where T : Avalonia.Controls.Control => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(settings).OfType<T>().Single(control => control.Name == name);
            var rail = Find<Avalonia.Controls.ListBox>("LspServer");
            E2E.E2eWorkspace.Until(() => rail.ItemCount == 2, "The settings rail did not list the fixture catalog.");
            void Select(string id) => rail.SelectedItem = rail.Items.Cast<object>().First(item => item?.ToString() == id);
            Select("typescript");
            Require(!Find<Avalonia.Controls.StackPanel>("LspTermsSection").IsVisible, "A server without terms showed the terms controls.");
            Select("intellij");
            E2E.E2eWorkspace.Until(() => Find<Avalonia.Controls.StackPanel>("LspTermsSection").IsVisible, "A server with terms showed no terms controls.");
            E2E.E2eWorkspace.Until(() => Find<Avalonia.Controls.TextBlock>("LspTermsStatus").Text!.StartsWith("Terms not accepted", StringComparison.Ordinal), "The unaccepted status was not shown: " + Find<Avalonia.Controls.TextBlock>("LspTermsStatus").Text);
            Require(Find<Avalonia.Controls.Button>("LspReviewTerms").IsEnabled && !Find<Avalonia.Controls.Button>("LspInstall").IsVisible, "Before acceptance the review button must be on and Install now hidden.");
            Require(terms.Count == 0, "Selecting a server that is not installed read its terms, which downloads it.");
            Find<Avalonia.Controls.Button>("LspReviewTerms").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => settings.TermsWindow is not null, "The review button opened no terms window.");
            var dialog = settings.TermsWindow!;
            E2E.E2eWorkspace.Until(() => terms.Count > 0 && Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.CheckBox>(dialog, "TermsConsent")!.IsEnabled, "The terms window did not load the terms.");
            Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.CheckBox>(dialog, "TermsConsent")!.IsChecked = true;
            E2E.E2eWorkspace.Until(() => Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(dialog, "AcceptTerms")!.IsEnabled);
            Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Button>(dialog, "AcceptTerms")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => terms.Any(request => request.Accept && request.Server == "intellij" && request.ArtifactId == Hash && request.EulaSha256 == Hash), "Accepting did not record the acknowledgement through the host call.");
            E2E.E2eWorkspace.Until(() => dialog is SharpRail.UI.Panels.LanguageTermsWindow { Result.Accepted: true }, "The terms window did not take the host's accepted reply before closing.");
            dialog.Close();
            E2E.E2eWorkspace.Until(() => Find<Avalonia.Controls.TextBlock>("LspTermsStatus").Text!.StartsWith("Terms accepted on 2026-11-07", StringComparison.Ordinal), "The accepted status was not shown.");
            Require(!Find<Avalonia.Controls.Button>("LspReviewTerms").IsEnabled, "The review button stayed on after acceptance.");
            E2E.E2eWorkspace.Until(() => Find<Avalonia.Controls.TextBlock>("LspResolved").Text!.StartsWith("None", StringComparison.Ordinal) && Find<Avalonia.Controls.Button>("LspInstall").IsVisible, "Install now was not offered after acceptance.");
            Find<Avalonia.Controls.Button>("LspInstall").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => provisions.Count == 1 && Find<Avalonia.Controls.TextBlock>("LspInstallStatus").Text!.Contains("Fixture download failed", StringComparison.Ordinal) && Find<Avalonia.Controls.Button>("LspInstallRetry").IsVisible, "A failed install showed no error and Retry.");
            Require(provisions[0].Server == "intellij" && provisions[0].Operation == "provision", "Install now did not call the provision path for the server.");
            failInstall = false;
            Find<Avalonia.Controls.Button>("LspInstallRetry").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => provisions.Count == 2 && Find<Avalonia.Controls.TextBlock>("LspInstallStatus").Text == "Installed." && !Find<Avalonia.Controls.Button>("LspInstall").IsVisible, "Retry did not install.");
            Select("typescript");
            Select("intellij");
            E2E.E2eWorkspace.Until(() => Find<Avalonia.Controls.TextBlock>("LspTermsStatus").Text!.StartsWith("Terms accepted on", StringComparison.Ordinal), "An installed, accepted server did not show its recorded acceptance.");
        }
        finally { settings.TermsWindow?.Close(); }
        Console.WriteLine("PASS Settings terms: status, review dialog records acceptance via the host, Install now with error and Retry, nothing for a server without terms");
    }

    private static string Licensing(JsonObject state) => Plugin(IntelliJLicensing.Project(state));

    private static string Plugin(JsonObject view)
    {
        view["busy"] = "";
        return view.ToJsonString();
    }
}