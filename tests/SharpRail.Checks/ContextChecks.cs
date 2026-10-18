using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Avalonia.Headless;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Client;
using SharpRail.Host.Remote;
using SharpRail.Host.Core.Context;

namespace SharpRail.Checks;

internal static partial class ContextChecks
{
    internal static async Task Run(string root)
    {
        var storage = Path.Combine(root, "context-provisioning");
        var bytes = "SharpRail fake Context executable; never run"u8.ToArray();
        var bundle = new ContextBundle("fixture", "fixture", new("https://download.jetbrains.com/sharprail-fixture"),
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var handler = new Handler(async token => { await Task.Delay(50, token); return bytes; });
        using var http = new HttpClient(handler);
        try
        {
            try
            {
                await ContextDistribution.InstallAsync(storage, bundle, http, default);
                throw new InvalidOperationException("Context downloaded before user authorization.");
            }
            catch (ContextConsentRequiredException) { }
            Require(handler.Requests == 0 && !Directory.Exists(storage), "An unauthorized request touched disk or network.");

            var consent = new ContextConsent(storage, http, bundle);
            var emptyStatus = await consent.ExecuteAsync(new("status"), default);
            Require(emptyStatus.State == "notInstalled" && handler.Requests == 0 && handler.LegalRequests == 0, "Context status performed network I/O or claimed installation.");
            var review = await consent.ReviewAsync(default);
            Require(review.State == "requiresAcceptance" && review.AcceptedAtUtc.Length == 0, "Context terms were preaccepted.");
            var accepted = await consent.ExecuteAsync(new("acceptAndDownload", review.ArtifactId, review.TermsSha256), default);
            Require(accepted.State == "downloaded" && accepted.AcceptedAtUtc.Length > 0, "Explicit fixture consent did not download.");
            Require((await new ContextConsent(storage, http, bundle).ReviewAsync(default)).AcceptedAtUtc == accepted.AcceptedAtUtc, "Context consent was not persisted.");
            var legalRequests = handler.LegalRequests;
            var installedStatus = await consent.ExecuteAsync(new("status"), default);
            Require(installedStatus.State == "downloaded" && installedStatus.Installation == accepted.Installation && handler.LegalRequests == legalRequests,
                "Installed status was lost or performed a terms request.");
            Require((await new ContextConsent(storage, http, bundle).ReviewAsync(default)).State == "downloaded", "Reopening forgot the verified download.");
            File.Delete(accepted.Installation);
            handler.Requests = 0;
            var paths = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ContextDistribution.InstallAsync(storage, bundle, http, default)));
            Require(paths.Distinct().Count() == 1 && handler.Requests == 1, "Concurrent provisioning did not share the verified installation.");
            await consent.ExecuteAsync(new("status"), default);
            var modifiedAt = File.GetLastWriteTimeUtc(paths[0]);
            var corrupted = bytes.ToArray(); corrupted[0] ^= 1;
            await File.WriteAllBytesAsync(paths[0], corrupted); File.SetLastWriteTimeUtc(paths[0], modifiedAt);
            try { await consent.AuthorizeExecutionAsync(default); throw new InvalidOperationException("Execution trusted display metadata instead of the full digest."); }
            catch (IOException error) when (error.Message.Contains("integrity", StringComparison.Ordinal)) { }
            File.SetLastWriteTimeUtc(paths[0], modifiedAt.AddSeconds(1));
            Require((await consent.ExecuteAsync(new("status"), default)).State == "repair", "Corrupt installation did not offer repair.");
            await ContextDistribution.InstallAsync(storage, bundle, http, default);
            Require(handler.Requests == 2 && (await File.ReadAllBytesAsync(paths[0])).SequenceEqual(bytes), "A corrupt installed binary was reused.");

            File.Delete(paths[0]);
            using var wrong = new HttpClient(new Handler(_ => Task.FromResult("wrong"u8.ToArray())));
            try
            {
                await ContextDistribution.InstallAsync(storage, bundle, wrong, default);
                throw new InvalidOperationException("Context accepted a hash mismatch.");
            }
            catch (IOException error) when (error.Message.Contains("SHA-256", StringComparison.Ordinal)) { }
            using var waiting = new HttpClient(new Handler(async token => { await Task.Delay(Timeout.Infinite, token); return bytes; }));
            using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try
            {
                await ContextDistribution.InstallAsync(storage, bundle, waiting, canceled.Token);
                throw new InvalidOperationException("Context ignored cancellation.");
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
            Require(!Directory.EnumerateFiles(storage, "*.partial", SearchOption.AllDirectories).Any(), "Failed provisioning left temporary downloads.");
            foreach (var platform in new[] { ("macos", "arm64"), ("macos", "x64"), ("linux", "arm64"), ("linux", "x64") })
            {
                var pinned = ContextDistribution.Bundle(platform.Item1, platform.Item2);
                Require(pinned.Version == ContextDistribution.Version && pinned.Sha256.Length == 64 && pinned.Url.Host == "download.jetbrains.com", "Unpinned Context download.");
            }
            handler.LegalText += " Changed fixture conditions.";
            var changed = await consent.ExecuteAsync(new("acceptAndDownload", review.ArtifactId, review.TermsSha256), default);
            Require(changed.State == "changed" && changed.AcceptedAtUtc.Length == 0, "Changed terms reused old acceptance.");
            try { await ContextDistribution.InstallAsync(storage, bundle, http, default); throw new IOException("Changed terms allowed provisioning."); }
            catch (ContextConsentRequiredException) { }
            try { await ContextDistribution.InstallAsync(storage, bundle with { Version = "different" }, http, default); throw new IOException("A new build reused old consent."); }
            catch (ContextConsentRequiredException) { }
            await TransportAsync(storage, bundle, bytes);
            await McpAsync(storage);
            await NativeWorkflowAsync(storage);
            Console.WriteLine("PASS Context provisioning fixtures: authorization before I/O, pinned integrity, concurrent reuse, corruption repair and cancellation cleanup");
        }
        finally { if (Directory.Exists(storage)) Directory.Delete(storage, true); }
    }

    private static async Task TransportAsync(string storage, ContextBundle bundle, byte[] bytes)
    {
        foreach (var remote in new[] { false, true })
        {
            var stateDirectory = Path.Combine(storage, remote ? "remote" : "local");
            await using var server = remote ? RemoteServer.Create(storage, IPAddress.Loopback, 0, "context-fixture", stateDirectory,
                plugins: seams => seams with { Builtins = [] }, headlessLanguageMcp: true) : null;
            if (server is not null) await server.StartAsync();
            var state = server is null ? new HostStateStore(stateDirectory) : (HostStateStore)server.Services.GetRequiredService<IHostStateService>();
            using var handler = new Handler(_ => Task.FromResult(bytes));
            using var http = new HttpClient(handler);
            state.ContextConsentOverride = new(Path.Combine(stateDirectory, "context"), http, bundle);
            using var proxy = server is null ? null : new RemoteProjectAdapter(new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), "context-fixture");
            IProjectServices host = proxy ?? (IProjectServices)new LocalProjectAdapter(new ProjectServices(storage, state));
            try
            {
                Require(!LanguageMcpTools.Create(state, storage).Any(tool => tool.Name.Contains("consent", StringComparison.OrdinalIgnoreCase) || tool.Name.Contains("accept", StringComparison.OrdinalIgnoreCase)), "Agent MCP exposed a consent action.");
                var review = await host.ContextConsentAsync(new());
                Require(review.State == "requiresAcceptance" && handler.Requests == 0, "Terms review downloaded a Context binary.");
                var missing = await host.ContextConsentAsync(new("download", review.ArtifactId, review.TermsSha256));
                Require(missing.State == "requiresAcceptance" && handler.Requests == 0, "Download without consent bypassed the host guard.");
                var accepted = await host.ContextConsentAsync(new("acceptAndDownload", review.ArtifactId, review.TermsSha256));
                Require(accepted.State == "downloaded" && handler.Requests == 1 && File.Exists(accepted.Installation), "UI consent/download transport failed.");
                Require((await host.ContextConsentAsync(new())).AcceptedAtUtc == accepted.AcceptedAtUtc, "Transport lost persisted consent.");
                var endpoint = server is null ? null : new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                await WorkflowTransportAsync(host, state, storage, stateDirectory, endpoint);
            }
            finally { if (server is null) await state.StopLanguagesAsync(); }
        }
        Console.WriteLine("PASS Context consent direct/WebSocket parity, review without download, explicit acceptance and persisted acknowledgement");
    }

    internal static void Ui(string root)
    {
        WorkflowUi(root);
        var storage = Path.Combine(root, "context-consent-ui");
        using var handler = new Handler(_ => Task.FromResult("synthetic fixture executable"u8.ToArray()));
        using var http = new HttpClient(handler);
        var bundle = new ContextBundle("fixture-ui", "fixture", new("https://download.jetbrains.com/fixture"),
            Convert.ToHexStringLower(SHA256.HashData("synthetic fixture executable"u8)));
        var state = new HostStateStore(Path.Combine(storage, "state"));
        state.ContextConsentOverride = new(Path.Combine(storage, "context"), http, bundle);
        var host = new LocalProjectAdapter(new ProjectServices(root, state));
        T Find<T>(Window window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        void Click(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var owner = new Window { Width = 560, Height = 520, Content = new SharpRail.UI.Panels.LanguagePanel(host, root, _ => Task.CompletedTask) };
        owner.Show();
        Click(Find<Button>(owner, "ContextSetup"));
        E2E.E2eWorkspace.Until(() => owner.OwnedWindows.OfType<SharpRail.UI.Panels.ContextConsentWindow>().Any());
        var canceled = owner.OwnedWindows.OfType<SharpRail.UI.Panels.ContextConsentWindow>().Single();
        E2E.E2eWorkspace.Until(() => Find<CheckBox>(canceled, "ConsentCheck").IsEnabled);
        Require(Find<CheckBox>(canceled, "ConsentCheck").IsChecked == false && !Find<Button>(canceled, "ConsentDownload").IsEnabled && handler.Requests == 0,
            "Context consent default was accepted or downloaded.");
        Click(Find<Button>(canceled, "ConsentClose"));
        Require(handler.Requests == 0, "Closing consent downloaded the CLI.");
        Click(Find<Button>(owner, "ContextSetup"));
        E2E.E2eWorkspace.Until(() => owner.OwnedWindows.OfType<SharpRail.UI.Panels.ContextConsentWindow>().Any());
        var dialog = owner.OwnedWindows.OfType<SharpRail.UI.Panels.ContextConsentWindow>().Single();
        try
        {
            E2E.E2eWorkspace.Until(() => Find<CheckBox>(dialog, "ConsentCheck").IsEnabled);
            if (Environment.GetEnvironmentVariable("SHARPRAIL_CONTEXT_CAPTURE") == "1")
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                using var frame = dialog.CaptureRenderedFrame();
                frame!.Save(".bench/context-consent/form.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            dialog.Width = 520; dialog.Height = 620; dialog.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var checkbox = Find<CheckBox>(dialog, "ConsentCheck");
            var point = checkbox.TranslatePoint(new Point(12, checkbox.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseMove(point);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var hit = dialog.InputHitTest(point) as Control;
            Require(hit is not null && (ReferenceEquals(hit, checkbox) || hit.GetVisualAncestors().Contains(checkbox)),
                $"Consent checkbox is occluded at {point} by {hit?.GetType().Name}; bounds {checkbox.Bounds}; window {dialog.Bounds}.");
            dialog.MouseDown(point, Avalonia.Input.MouseButton.Left);
            dialog.MouseUp(point, Avalonia.Input.MouseButton.Left);
            E2E.E2eWorkspace.Until(() => checkbox.IsChecked == true);
            Require(Find<Button>(dialog, "ConsentDownload").IsEnabled, "Checking consent did not enable its action.");
            Click(Find<Button>(dialog, "ConsentDownload"));
            E2E.E2eWorkspace.Until(() => Find<TextBlock>(dialog, "ConsentStatus").Text!.Contains("Downloaded and verified", StringComparison.Ordinal));
            Require(Equals(Find<Button>(dialog, "ConsentClose").Content, "Done") && handler.Requests == 1 && Find<CheckBox>(dialog, "ConsentCheck").IsChecked == true && !Find<CheckBox>(dialog, "ConsentCheck").IsEnabled,
                "Explicit UI consent was not persisted and reflected immediately.");
            Require(Equals(Find<Button>(owner, "ContextSetup").Content, "View installation…"), "The pane kept offering download after completion.");
            Click(Find<Button>(dialog, "ConsentClose"));
            E2E.E2eWorkspace.Until(() => Find<Button>(owner, "ContextSetup").IsEnabled);
            Click(Find<Button>(owner, "ContextSetup"));
            E2E.E2eWorkspace.Until(() => owner.OwnedWindows.OfType<SharpRail.UI.Panels.ContextConsentWindow>().Any());
            var reopened = owner.OwnedWindows.OfType<SharpRail.UI.Panels.ContextConsentWindow>().Single();
            E2E.E2eWorkspace.Until(() => Equals(Find<Button>(reopened, "ConsentClose").Content, "Done"));
            Require(!Find<Button>(reopened, "ConsentDownload").IsVisible && handler.Requests == 1, "Reopening offered or repeated an unnecessary download.");
            reopened.Close();
        }
        finally { dialog.Close(); owner.Close(); state.StopLanguagesAsync().AsTask().GetAwaiter().GetResult(); }
        Console.WriteLine("PASS Context consent window: unchecked default, cancel without download, explicit accept/download and visible completion");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static async Task McpAsync(string storage)
    {
        var fixture = Path.Combine(storage, "mcp.mjs");
        await File.WriteAllTextAsync(fixture, """
            import readline from 'node:readline';
            let initialization,root;
            const send=m=>process.stdout.write(JSON.stringify({jsonrpc:'2.0',...m})+'\n');
            readline.createInterface({input:process.stdin}).on('line',line=>{
              const m=JSON.parse(line);
              if(m.method==='initialize'){initialization=m.id;send({id:100,method:'roots/list'});}
              else if(m.id===100){root=m.result.roots[0].uri;send({id:initialization,result:{protocolVersion:'2025-06-18',capabilities:{tools:{}}}});}
              else if(m.method==='tools/list')send({id:m.id,result:{tools:[{name:'code_search',inputSchema:{type:'object'}}]}});
              else if(m.method==='tools/call' && m.params.arguments.query!=='wait')send({id:m.id,result:{content:[{type:'text',text:root}]}});
            });
            """);
        ProcessStartInfo Start()
        {
            var start = new ProcessStartInfo("node");
            start.ArgumentList.Add(fixture);
            return start;
        }
        var workspaces = new[] { Path.Combine(storage, "workspace-a"), Path.Combine(storage, "workspace-b") };
        foreach (var workspace in workspaces)
        {
            Directory.CreateDirectory(workspace);
            await using var connection = new ContextMcpConnection(Start(), workspace);
            var tools = await connection.InitializeAsync(default);
            Require(tools["tools"]?[0]?["name"]?.GetValue<string>() == "code_search", "The Context MCP tools were not discovered.");
            var result = await connection.SearchAsync(new JsonObject { ["query"] = "fixture" }, default);
            Require(result["content"]?[0]?["text"]?.GetValue<string>() == new Uri(Path.GetFullPath(workspace) + Path.DirectorySeparatorChar).AbsoluteUri,
                "Context received roots from a different workspace.");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try
            {
                await connection.SearchAsync(new JsonObject { ["query"] = "wait" }, cancellation.Token);
                throw new InvalidOperationException("Context search ignored cancellation.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        Console.WriteLine("PASS Context stdio fixtures: initialization, code_search forwarding, single workspace roots and owned-process cancellation");
    }

    private sealed class Handler(Func<CancellationToken, Task<byte[]>> response) : HttpMessageHandler
    {
        internal int Requests;
        internal int LegalRequests;
        internal string LegalText = "Synthetic fixture agreement, never JetBrains terms. " + new string('x', 120);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/legal/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref LegalRequests);
                return new(HttpStatusCode.OK) { Content = new StringContent("<div class=\"licenseContent\"><p>" + LegalText + "</p></div>"), RequestMessage = request };
            }
            Interlocked.Increment(ref Requests);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(await response(cancellationToken)), RequestMessage = request };
        }
    }
}