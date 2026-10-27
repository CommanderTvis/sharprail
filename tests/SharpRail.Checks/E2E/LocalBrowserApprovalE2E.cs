using System.Net;
using System.Net.Http.Json;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;

using SharpRail.Host.Protocol;
using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class LocalBrowserApprovalE2E
{
    internal static Window? Prompt(Window root) => root.OwnedWindows.SelectMany(child => new[] { child }.Concat(child.OwnedWindows)).FirstOrDefault(child => Equals(child.Tag, "LocalBrowserApproval"));
    internal static T Field<T>(Control root, string name) where T : Control => root.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    internal static void Run(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "local-browser-ui"), serving: true);
        var listener = app.Workbench.Listener!;
        var start = listener.StartAsync(IPAddress.Loopback, 0, listener.Token);
        Until(() => start.IsCompleted); start.GetAwaiter().GetResult();
        using var client = new HttpClient { BaseAddress = listener.Endpoint };
        var settings = new SettingsWindow(app.Window, () => { }, section: "Host");
        app.Window.ShowInOverlay(settings);
        var denied = Task.Run(() => LocalBrowserConnectionChecks.Request(client));
        Until(() => Prompt(app.Window) is not null);
        var prompt = Prompt(app.Window)!;
        Require(Field<SelectableTextBlock>(prompt, "LocalBrowserOrigin").Text == LocalBrowserConnectionChecks.Origin && Field<Button>(prompt, "LocalBrowserDeny").IsFocused,
            "Consent must display the real origin and focus Deny.");
        app.Click(Field<Button>(prompt, "LocalBrowserDeny"));
        Until(() => denied.IsCompleted);
        using (var response = denied.GetAwaiter().GetResult()) Require(response.StatusCode == HttpStatusCode.Forbidden, "Deny must reject the request.");
        Settle(2200);
        var dismissed = Task.Run(() => LocalBrowserConnectionChecks.Request(client));
        Until(() => Prompt(app.Window) is not null);
        Prompt(app.Window)!.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Until(() => dismissed.IsCompleted && Prompt(app.Window) is null);
        using (var response = dismissed.GetAwaiter().GetResult()) Require(response.StatusCode == HttpStatusCode.Forbidden && listener.BrowserConnections!.Origins.Count == 0, "Escape must deny without issuing a credential.");
        Settle(2200);
        using var cancellation = new CancellationTokenSource();
        var cancelled = Task.Run(() => LocalBrowserConnectionChecks.Request(client, cancellationToken: cancellation.Token));
        Until(() => Prompt(app.Window) is not null);
        cancellation.Cancel();
        Until(() => Prompt(app.Window) is null && cancelled.IsCompleted);
        Require(listener.BrowserConnections!.Origins.Count == 0, "Cancelling the browser request must dismiss its prompt without granting access.");
        var approved = Task.Run(() => LocalBrowserConnectionChecks.Request(client));
        Until(() => Prompt(app.Window) is not null);
        app.Click(Field<Button>(Prompt(app.Window)!, "LocalBrowserAllow"));
        Until(() => approved.IsCompleted && Field<StackPanel>(settings, "HostBrowsers").Children.Count == 1);
        using var result = approved.GetAwaiter().GetResult();
        var read = Task.Run(async () => (await result.Content.ReadFromJsonAsync<LocalBrowserApprovalReply>())!.Token);
        Until(() => read.IsCompleted);
        Require(read.Result != listener.Token && Field<TextBlock>(settings, "LocalBrowserConnectionOrigin").Text == LocalBrowserConnectionChecks.Origin,
            "Settings must show approved origins without exposing their credentials.");
        app.Click(Field<Button>(settings, "LocalBrowserRevoke"));
        Until(() => Field<StackPanel>(settings, "HostBrowsers").Children.Count == 0);
        Require(listener.BrowserConnections.Origins.Count == 0, "Revoke access must remove the browser grant.");
        var awaiting = Task.Run(() => LocalBrowserConnectionChecks.Request(client));
        Until(() => Prompt(app.Window) is not null);
        var stop = listener.StopAsync();
        Until(() => stop.IsCompleted && awaiting.IsCompleted && Prompt(app.Window) is null);
        stop.GetAwaiter().GetResult();
        using (var stopped = awaiting.GetAwaiter().GetResult()) Require(stopped.StatusCode == HttpStatusCode.ServiceUnavailable, "Stopping the listener must cancel its pending approval.");
        settings.Close();
        Console.WriteLine("PASS local browser consent UI: true origin, Deny focus, deny/cancel/allow, Settings projection/revocation and stop dismisses pending consent");
    }
}