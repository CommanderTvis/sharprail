using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

using Avalonia.Controls;

using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

// A headless app controlled over stdin by the real Chromium journey. No test HTTP routes are exposed.
internal static class LocalBrowserApprovalFixture
{
    internal static void Run(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "browser-consent-fixture"), serving: true);
        var listener = app.Workbench.Listener!;
        void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        Wait(listener.StartAsync(IPAddress.Loopback, 0, "local-browser-fixture-master"));
        var commands = new ConcurrentQueue<string>();
        _ = Task.Run(() => { while (Console.ReadLine() is { } line) commands.Enqueue(line); commands.Enqueue("quit"); });
        Window? previous = null;
        var prompts = 0;
        void Reply(string kind)
        {
            var prompt = LocalBrowserApprovalE2E.Prompt(app.Window);
            Console.WriteLine("LOCAL_BROWSER_FIXTURE " + JsonSerializer.Serialize(new
            {
                kind,
                endpoint = listener.Endpoint?.ToString(),
                pending = prompt is not null,
                origin = prompt is null ? null : LocalBrowserApprovalE2E.Field<SelectableTextBlock>(prompt, "LocalBrowserOrigin").Text,
                approved = listener.BrowserConnections?.Origins.Count ?? 0,
                prompts
            }));
        }
        Reply("ready");
        while (true)
        {
            var prompt = LocalBrowserApprovalE2E.Prompt(app.Window);
            if (prompt is not null && !ReferenceEquals(prompt, previous)) prompts++;
            previous = prompt;
            if (commands.TryDequeue(out var command))
            {
                if (command == "quit") break;
                if (command is "allow" or "deny")
                {
                    Require(prompt is not null, "There is no pending approval.");
                    app.Click(LocalBrowserApprovalE2E.Field<Button>(prompt!, command == "allow" ? "LocalBrowserAllow" : "LocalBrowserDeny"));
                }
                else if (command == "revoke")
                {
                    var settings = new SettingsWindow(app.Window, () => { }, section: "Host");
                    settings.Show(app.Window); Settle();
                    app.Click(LocalBrowserApprovalE2E.Field<Button>(settings, "LocalBrowserRevoke"));
                    settings.Close();
                }
                else if (command == "restart")
                {
                    var port = listener.Port;
                    Wait(listener.StopAsync());
                    Wait(listener.StartAsync(IPAddress.Loopback, port, listener.Token));
                }
                Settle(20);
                Reply(command);
            }
            Settle(10);
        }
    }
}