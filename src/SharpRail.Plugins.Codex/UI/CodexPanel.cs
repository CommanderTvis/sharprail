using System.Globalization;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Codex.UI;

/// <summary>
/// The Codex side tool: Context (the AGENTS.md chain and the offers), Settings (the trust line and the layered keys),
/// Capabilities (the status hooks and MCP servers) and Account, with a re-read button and the warnings above whichever
/// surface is shown.
/// </summary>
public static class CodexPanel
{
    private enum Surface
    {
        Context,
        Settings,
        Capabilities,
        Account
    }

    private sealed record Offer(CodexInstructionsTarget Target, string Name, Func<CodexConfigSnapshot, bool> Missing);

    private static readonly Offer[] Offers =
    [
        new(CodexInstructionsTarget.Project, "CodexOffer_project",
            snapshot => snapshot.Instructions.All(file => file.Scope != CodexInstructionsScope.Project)),
        new(CodexInstructionsTarget.ProjectOverride, "CodexOffer_project_override",
            snapshot => snapshot.Instructions.All(file => file.RelativePath != "AGENTS.override.md")),
        new(CodexInstructionsTarget.Global, "CodexOffer_global",
            snapshot => snapshot.Instructions.All(file => file.Scope != CodexInstructionsScope.Global))
    ];

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : Math.Round(bytes / 1024.0, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.CurrentCulture) + " KB";

    public static Control Create(IPluginUIContext context, string workspaceId)
    {
        CodexConfigSnapshot? snapshot = null;
        string? error = null;
        var surface = Surface.Context;
        var root = new CodexPanelFrame();
        var segmentButtons = new Dictionary<Surface, ToggleButton>();
        var refresh = root.FindControl<Button>("CodexConfigRefresh")!;
        refresh.Content = Ui.Icon("refresh");
        var notices = root.FindControl<StackPanel>("Notices")!;
        var errorText = root.FindControl<TextBlock>("CodexConfigError")!;
        var body = root.FindControl<ScrollViewer>("Body")!;
        var scope = root.FindControl<StackPanel>("CodexConfigScope")!;
        var scopeHeading = root.FindControl<TextBlock>("CodexConfigScopeHeading")!;
        var scopeDetail = root.FindControl<TextBlock>("CodexConfigScopeDetail")!;
        var (session, activeTab) = Shown(context.Host(), workspaceId);
        var generation = 0;
        foreach (var option in Enum.GetValues<Surface>())
        {
            var segment = root.FindControl<ToggleButton>("CodexSurface_" + option.ToString().ToLowerInvariant())!;
            segment.Click += (_, _) => { surface = option; Render(); };
            segmentButtons[option] = segment;
        }
        refresh.Click += (_, _) =>
        {
            if (surface == Surface.Account) Render();
            else Refresh();
        };

        // Only the latest read lands, so a slow read for a tab no longer shown cannot overwrite the current one.
        void Run(Func<Task<CodexConfigSnapshot>> call) => _ = RunAsync(++generation, call);
        async Task RunAsync(int revision, Func<Task<CodexConfigSnapshot>> call)
        {
            try
            {
                var next = await call();
                Dispatcher.UIThread.Post(() => { if (revision != generation) return; snapshot = next; error = null; Render(); });
            }
            catch (Exception failure)
            {
                Dispatcher.UIThread.Post(() => { if (revision != generation) return; error = failure.Message; Render(); });
            }
        }
        CodexConfigParams Scope() => new(workspaceId) { TabKey = session?.TabKey };
        void Refresh() => Run(async () => await context.RequestAsync(CodexContract.ConfigGet, Scope()));

        void Open(string path) => _ = OpenAsync(path);
        async Task OpenAsync(string path)
        {
            try
            {
                if (await context.Editors.OpenAsync(workspaceId, path) is null) throw new IOException();
            }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, $"Couldn't open {ScopedSetting.AbbreviateHomePath(path)}"); }
        }

        async Task OfferAsync(CodexInstructionsTarget target)
        {
            try
            {
                var created = await context.RequestAsync(CodexContract.CreateInstructions, new CodexCreateInstructionsParams(workspaceId, target));
                if (created.RelativePath is { } relative) await context.Editors.OpenAsync(workspaceId, relative);
                else context.Notify(PluginNotificationKind.Info, $"Created {created.Path}");
                Refresh();
            }
            catch (Exception failure) { Dispatcher.UIThread.Post(() => { error = failure.Message; Render(); }); }
        }

        void Save(CodexWritableScope scope, IReadOnlyList<string> keyPath, JsonElement? value) =>
            Run(async () =>
            {
                await context.RequestAsync(CodexContract.SetValue, new CodexSetValueParams(workspaceId, scope, keyPath) { Value = value });
                return await context.RequestAsync(CodexContract.ConfigGet, Scope());
            });

        void Render()
        {
            foreach (var (option, segment) in segmentButtons) segment.IsChecked = option == surface;
            var tip = surface == Surface.Account ? "Refresh account and usage" : "Re-read configuration";
            ToolTip.SetTip(refresh, tip);
            Avalonia.Automation.AutomationProperties.SetName(refresh, tip);

            scope.IsVisible = surface == Surface.Context;
            scope.Tag = session is not null ? "session" : "workspace";
            if (session is not null)
            {
                scopeHeading.Text = $"Context of Codex in tab {session.Title}";
                scopeDetail.Text = TerminalFacts.CwdLabel(snapshot?.Root) ?? "…";
                scopeDetail.FontFamily = Ui.CodeFont;
                scopeDetail.Foreground = Ui.Muted;
                ToolTip.SetTip(scopeDetail, snapshot?.Root ?? "Reading the directory…");
            }
            else
            {
                scopeHeading.Text = activeTab is not null ? $"No Codex in tab {activeTab.Title}" : "No terminal open";
                scopeDetail.Text = "Showing what a new session here would load";
                scopeDetail.ClearValue(TextBlock.FontFamilyProperty);
                scopeDetail.Foreground = Ui.Hint;
                ToolTip.SetTip(scopeDetail, null);
            }
            errorText.Text = error;
            errorText.IsVisible = error is not null;
            notices.Children.Clear();
            foreach (var (detail, path) in Problems())
            {
                var problem = new CodexProblemNotice();
                problem.FindControl<ContentControl>("WarningIcon")!.Content = Ui.Icon("alertWarning", Ui.Warning, 14);
                problem.FindControl<TextBlock>("Detail")!.Text = detail;
                var source = problem.FindControl<ContentControl>("Source")!;
                if (path is null) source.IsVisible = false;
                else source.Content = ScopedSetting.SourcePath(path, Open);
                notices.Children.Add(problem);
            }

            body.Content = surface == Surface.Account ? CodexAccountSurface.Create(context)
                : snapshot is null ? Padded(Ui.Text("Reading Codex configuration…", Ui.Muted, 13))
                : surface switch
                {
                    Surface.Context => ContextSurface(snapshot),
                    Surface.Settings => SettingsSurface(snapshot),
                    _ => CapabilitiesSurface(snapshot)
                };
        }

        IEnumerable<(string Detail, string? Path)> Problems()
        {
            foreach (var layer in snapshot?.Layers ?? [])
            {
                if (layer.Error is { } failure) yield return (failure, layer.Path);
                else if (layer.Ignored) yield return ("Skipped: Codex does not trust this project yet, so it ignores this file.", layer.Path);
            }
            if (snapshot is { HooksInstalled: true, HooksTrusted: false })
                yield return ("Codex skips the status hooks until you trust them: run /hooks inside Codex once, then refresh.", null);
        }

        Control ContextSurface(CodexConfigSnapshot current)
        {
            var frame = new CodexContextFrame();
            var offers = frame.FindControl<StackPanel>("Offers")!;
            foreach (var offer in Offers)
            {
                var button = frame.FindControl<Button>(offer.Name)!;
                if (!offer.Missing(current)) { offers.Children.Remove(button); continue; }
                button.Click += (_, _) => _ = OfferAsync(offer.Target);
            }
            frame.FindControl<TextBlock>("ContextSize")!.Text = FormatSize(current.Instructions.Sum(file => file.Bytes));
            var rows = frame.FindControl<StackPanel>("Instructions")!;
            foreach (var file in current.Instructions)
            {
                var row = new CodexInstructionsRow { Tag = file.Path };
                row.FindControl<ContentControl>("FileIcon")!.Content = Ui.Icon("fileText", Ui.Hint, 14);
                row.FindControl<TextBlock>("FileName")!.Text = Path.GetFileName(file.Path);
                row.FindControl<ContentControl>("Scope")!.Content = file.Scope == CodexInstructionsScope.Launch ? LaunchFlagChip()
                    : ScopedSetting.ScopeChip(file.Scope == CodexInstructionsScope.Global ? "user" : "project", "CodexInstructionsScope");
                row.FindControl<ContentControl>("Source")!.Content = ScopedSetting.SourcePath(file.Path, _ => Open(file.RelativePath ?? file.Path), "CodexInstructionsSource");
                row.FindControl<TextBlock>("CodexInstructionsSize")!.Text = FormatSize(file.Bytes);
                rows.Children.Add(row);
            }
            return frame;
        }

        Control SettingsSurface(CodexConfigSnapshot current) => CodexSettingsSurface.Create(current.ProjectTrusted, current.Settings, Save, Open);

        Control CapabilitiesSurface(CodexConfigSnapshot current)
        {
            var frame = new CodexCapabilitiesFrame();
            frame.FindControl<ContentControl>("HooksAction")!.Content = current.HooksInstalled
                ? ScopedSetting.ScopeChip("user")
                : ScopedSetting.RowAction("CodexInstallHooks", "Install",
                    () => Run(async () => await context.RequestAsync(CodexContract.InstallHooks, Scope())));
            frame.FindControl<TextBlock>("HooksDetail")!.Text = current.HooksInstalled
                ? current.HooksTrusted ? "Installed and trusted." : "Installed; waiting for you to trust them in Codex."
                : "Report running, waiting and done to SharpRail. Inert outside a SharpRail terminal.";
            var rows = frame.FindControl<StackPanel>("Servers")!;
            foreach (var server in current.McpServers)
            {
                var row = new CodexMcpRow();
                row.FindControl<TextBlock>("ServerName")!.Text = server.Name;
                row.FindControl<TextBlock>("Target")!.Text = server.Target;
                row.FindControl<ContentControl>("Scope")!.Content = ScopedSetting.ScopeChip(server.Scope.ToString().ToLowerInvariant());
                row.FindControl<ContentControl>("Source")!.Content = ScopedSetting.SourcePath(server.Path, Open);
                rows.Children.Add(row);
            }
            return frame;
        }

        // The pane describes the first shown Codex terminal; another tab or a relaunch rereads its configuration.
        IDisposable? watch = null;
        root.AttachedToVisualTree += (_, _) => watch ??= context.WatchHost(host =>
        {
            var (found, active) = Shown(host, workspaceId);
            return (found?.TabKey, found?.Title, found?.Agent?.Command, active?.TabKey, active?.Title);
        }, (next, previous) =>
        {
            (session, activeTab) = Shown(context.Host(), workspaceId);
            if (next.Item1 != previous.Item1 || next.Item3 != previous.Item3) Refresh();
            else Render();
        });
        root.DetachedFromVisualTree += (_, _) => { watch?.Dispose(); watch = null; };

        Render();
        Refresh();
        return root;
    }

    private static (TerminalTabInfo? Session, TerminalTabInfo? Active) Shown(PluginHostProjection host, string workspaceId)
    {
        var tabs = host.Terminals.GetValueOrDefault(workspaceId) ?? [];
        var shown = (host.ShownTerminalTabKeys.GetValueOrDefault(workspaceId) ?? []).Select(key => tabs.FirstOrDefault(tab => tab.TabKey == key))
            .OfType<TerminalTabInfo>().ToArray();
        return (shown.FirstOrDefault(tab => tab.Agent?.Kind == CodexManifest.Id), shown.FirstOrDefault());
    }

    private static Control LaunchFlagChip()
    {
        var chip = new Border
        {
            Name = "CodexLaunchFlagChip",
            Background = Ui.SuccessWash,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Text("LAUNCH FLAG", Ui.Success, 10)
        };
        ToolTip.SetTip(chip, "Added to this session with developer_instructions");
        return chip;
    }

    private static Control Padded(Control child)
    {
        child.Margin = new Thickness(8);
        return child;
    }
}