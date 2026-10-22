using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

internal static class GlobalJvmProviderChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task Run(string root)
    {
        foreach (var (name, values, selected, expected) in new[]
        {
            ("empty", Array.Empty<bool>(), "", "standard"),
            ("all-intellij", new[] { true, true }, "", "intellij"),
            ("all-standard", new[] { false, false }, "", "standard"),
            ("conflict", new[] { true, false }, "", "standard"),
            ("explicit", new[] { true, true }, "standard", "standard")
        })
        {
            var directory = Path.Combine(root, "global-jvm-migration", name);
            var state = new HostStateStore(directory, () => new()
            {
                Settings = new() { JavaKotlinProvider = selected },
                WorkspaceLanguages = values.Select((value, index) => (value, index)).ToDictionary(item => Path.Combine(directory, item.index.ToString()),
                    item => "{\"intellij\":{\"useForJavaAndKotlin\":" + item.value.ToString().ToLowerInvariant() + "}}")
            });
            Require(state.Current.Settings.JavaKotlinProvider == expected && new HostStateStore(directory).Current.Settings.JavaKotlinProvider == expected,
                "Global provider migration/reload precedence failed: " + name);
        }
        foreach (var remote in new[] { false, true }) await Exercise(Path.Combine(root, "global-jvm", remote ? "remote" : "local"), remote);
    }

    private static async Task Exercise(string root, bool remote)
    {
        var left = Path.Combine(root, "left"); var right = Path.Combine(root, "right");
        Directory.CreateDirectory(left); Directory.CreateDirectory(right);
        var storage = Path.Combine(root, "state");
        await using var server = remote ? RemoteServer.Create(left, IPAddress.Loopback, 0, "global-jvm-test", storage, plugins: seams => seams with { Builtins = SharpRail.Host.Core.Plugins.BuiltinPlugins.LanguageServerEntries }) : null;
        if (server is not null) await server.StartAsync();
        var state = server is null ? new HostStateStore(storage) : (HostStateStore)server.Services.GetRequiredService<IHostStateService>();
        var endpoint = server is null ? null : new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var remoteHost = endpoint is null ? null : new RemoteProjectAdapter(endpoint, "global-jvm-test");
        IProjectServices host = remoteHost ?? (IProjectServices)new LocalProjectAdapter(new ProjectServices(left, state));
        var disabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE"); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        try
        {
            var executable = Environment.ProcessPath!;
            string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
            await state.ChangeAsync([HostStateChange.Setting("language-server:java", JsonSerializer.Serialize(new LanguageServerOptions(Command: executable, Arguments: arguments), JsonSerializerOptions.Web))]);
            var competingPids = new List<int>();
            foreach (var workspace in new[] { left, right })
            {
                await host.LanguageAsync(new(workspace, Operation: "configure", Server: "intellij", Configuration: JsonSerializer.Serialize(new
                {
                    useForJavaAndKotlin = true,
                    installationPath = IntelliJTermsChecks.Installation(Path.Combine(workspace, ".tools", "installation")),
                    projectJdk = "/host/project-jdk"
                })));
                Require((await host.LanguageAsync(new(workspace, Operation: "retry", Server: "intellij"))).State == "disabled", "Legacy workspace opt-in overrode the global choice.");
                await host.LanguageAsync(new(workspace, Operation: "retry", Server: "java"));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while ((await host.LanguageAsync(new(workspace, Operation: "status", Server: "java"), timeout.Token)).State != "ready") await Task.Delay(30, timeout.Token);
                competingPids.Add(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace, ".tools", "initialize.json")))!["pid"]!.GetValue<int>());
            }
            await host.LanguageAsync(new(left, Operation: "setJavaKotlinProvider", Configuration: "intellij"));
            foreach (var pid in competingPids)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    Require(process.HasExited, "Global provider switch left a competing JVM process running.");
                }
                catch (ArgumentException) { }
            }
            Require((await host.LanguageAsync(new(right, Operation: "javaKotlinProvider"))).Configuration == "intellij", "Provider selection stayed workspace-specific.");
            foreach (var workspace in new[] { left, right })
            {
                var rows = await host.LanguageAsync(new(workspace, Operation: "servers"));
                Require(rows.Servers.Single(item => item.Id == "java").State == "disabled" && rows.Servers.Single(item => item.Id == "kotlin").State == "disabled", "Global selection did not suspend all competing providers.");
                Require((await host.LanguageAsync(new(workspace, Operation: "configuration", Server: "intellij"))).Configuration.Contains("/host/project-jdk", StringComparison.Ordinal), "Global choice overwrote workspace provider options.");
                await host.LanguageAsync(new(workspace, Operation: "retry", Server: "intellij"));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                LanguageResult result;
                do { result = await host.LanguageAsync(new(workspace, Operation: "status", Server: "intellij"), timeout.Token); if (result.State is "installing" or "initializing") await Task.Delay(30, timeout.Token); }
                while (result.State is "installing" or "initializing");
                Require(result.State == "requiresAcceptance", "Global choice bypassed installation-specific consent.");
            }
            using var remoteState = endpoint is null ? null : new RemoteStateAdapter(endpoint, "global-jvm-test");
            Require((remoteState is null ? state.Current : await remoteState.GetStateAsync()).Settings.JavaKotlinProvider == "intellij" && new HostStateStore(storage).Current.Settings.JavaKotlinProvider == "intellij", "Global choice was lost in snapshots or reload.");
            await host.LanguageAsync(new(right, Operation: "setJavaKotlinProvider", Configuration: "standard"));
            Require((await host.LanguageAsync(new(left, Operation: "servers"))).Servers.Single(item => item.Id == "java").State == "idle", "Global fallback did not restore standard routing.");
            Console.WriteLine("PASS global JVM provider migration, exclusivity, consent and persistence " + (remote ? "remote" : "local"));
        }
        finally { await state.StopLanguagesAsync(); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled); }
    }

    internal static void RunUi(string root)
    {
        using var app = new E2E.E2eWorkspace(Path.Combine(root, "global-jvm-ui"), openFiles: false);
        var settings = new SharpRail.UI.Panels.SettingsWindow(app.Window, () => { }, section: "Code Intelligence");
        settings.Show(app.Window);
        T Find<T>(string name) where T : Control => settings.GetLogicalDescendants().OfType<T>().Single(item => item.Name == name);
        try
        {
            E2E.E2eWorkspace.Until(() => Find<ComboBox>("JvmProvider").SelectedIndex == 0);
            Find<ComboBox>("JvmProvider").SelectedIndex = 1;
            Find<Button>("ApplyJvmProvider").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => app.State!.Current.Settings.JavaKotlinProvider == "intellij" && Find<ComboBox>("JvmProvider").IsEnabled);
            app.State!.ChangeAsync([HostStateChange.Setting("java-kotlin-provider", "standard")]).AsTask().GetAwaiter().GetResult();
            E2E.E2eWorkspace.Until(() => Find<ComboBox>("JvmProvider").SelectedIndex == 0);
            Require(settings.GetLogicalDescendants().OfType<TextBlock>().Any(item => item.Text == "Java / Kotlin provider · all projects on this host"), "Provider UI still claims workspace scope.");
        }
        finally { settings.Close(); }
        Console.WriteLine("PASS global JVM provider settings save and broadcast refresh");
    }
}