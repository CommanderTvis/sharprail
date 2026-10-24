using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Omp.UI;

/// <summary>
/// The omp side tool: the Early badge, the installed version or the missing-binary state with the install link, which
/// code intelligence sessions use, the profile note, and the context files omp reads for the workspace with token
/// estimates, imports nested under their importer, and offers to add missing files. All reads run on the host.
/// </summary>
public static class OmpPanel
{
    public static string CodeIntelligenceNote(OmpSettings settings) =>
        settings.OwnCodeIntelligence ? "Code intelligence: omp (its own language servers)" : "Code intelligence: SharpRail (launched with --no-lsp; use the lsp_* tools)";

    public static string? ProfileNote(OmpSettings settings) => settings.Mcp
        ? $"Launches use the \"{OmpLaunch.Profile}\" omp profile so SharpRail's MCP server is configured. A profile keeps its own sign-in, settings, " +
          "sessions and user context files: run /login once in a SharpRail omp tab, or use provider API key variables."
        : null;

    public static Control Create(IPluginUIContext context, string workspaceId)
    {
        var root = new OmpPanelFrame();
        var refresh = root.FindControl<Button>("OmpRefresh")!;
        refresh.Content = Ui.Icon("refresh");
        var version = root.FindControl<TextBlock>("OmpVersion")!;
        var missing = root.FindControl<StackPanel>("OmpMissing")!;
        var missingText = root.FindControl<TextBlock>("OmpMissingText")!;
        var install = root.FindControl<ContentControl>("OmpInstallLink")!;
        var codeIntelligence = root.FindControl<TextBlock>("OmpCodeIntelligence")!;
        var profileNote = root.FindControl<TextBlock>("OmpProfileNote")!;
        var error = root.FindControl<TextBlock>("OmpError")!;
        var rows = root.FindControl<StackPanel>("OmpContextRows")!;
        var offers = root.FindControl<WrapPanel>("OmpOffers")!;
        var link = Ui.Button("Install from omp.sh", () => TopLevel.GetTopLevel(root)?.Launcher.LaunchUriAsync(new Uri(OmpLaunch.InstallUrl)));
        link.Name = "OmpInstall";
        install.Content = link;
        var generation = 0;

        void Notes(OmpSettings settings)
        {
            codeIntelligence.Text = CodeIntelligenceNote(settings);
            profileNote.Text = ProfileNote(settings);
            profileNote.IsVisible = profileNote.Text is not null;
        }

        void Open(string path) => _ = OpenAsync(path);
        async Task OpenAsync(string path)
        {
            try
            {
                if (await context.Editors.OpenAsync(workspaceId, path) is null) throw new IOException();
            }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, $"Couldn't open {ScopedSetting.AbbreviateHomePath(path)}"); }
        }

        void ShowContext(OmpContextSnapshot snapshot)
        {
            rows.Children.Clear();
            if (snapshot.Files.Count == 0) rows.Children.Add(Ui.Text("omp reads no context files here yet.", Ui.Hint, 12));
            foreach (var file in snapshot.Files)
            {
                var row = new DockPanel { Name = "OmpContextRow", Tag = file.Path, Margin = new Thickness(file.Depth * 16, 0, 0, 0) };
                var tokens = Ui.Text(file.Shadowed ? "shadowed" : file.Cycle ? "cycle" : "~" + file.Tokens.ToString("N0", CultureInfo.CurrentCulture) + " tok", Ui.Hint, 12);
                tokens.Name = "OmpContextTokens";
                DockPanel.SetDock(tokens, Dock.Right);
                row.Children.Add(tokens);
                var scope = ScopedSetting.ScopeChip(file.Depth > 0 ? "import" : file.Scope.ToString().ToLowerInvariant(), "OmpContextScope");
                scope.Margin = new Thickness(0, 0, 6, 0);
                DockPanel.SetDock(scope, Dock.Left);
                row.Children.Add(scope);
                var source = ScopedSetting.SourcePath(file.Path, Open, "OmpContextSource");
                source.Opacity = file.Shadowed || file.Cycle ? 0.6 : 1;
                ToolTip.SetTip(source, file.Path + (file.Depth > 0 ? "" : $" ({file.Provider})"));
                row.Children.Add(source);
                rows.Children.Add(row);
            }
            offers.Children.Clear();
            foreach (var offer in snapshot.Offers)
            {
                var button = Ui.Button(offer.Label, () => _ = CreateAsync(offer.Target), "add");
                button.Name = "OmpOffer_" + offer.Target.ToString().ToLowerInvariant();
                button.Margin = new Thickness(0, 0, 8, 8);
                offers.Children.Add(button);
            }
        }

        async Task CreateAsync(OmpContextTarget target)
        {
            try
            {
                var snapshot = await context.RequestAsync(OmpContract.CreateContextFile, new OmpCreateParams(workspaceId, target));
                Dispatcher.UIThread.Post(() => ShowContext(snapshot));
            }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't create the omp context file"); }
        }

        void Refresh()
        {
            var current = ++generation;
            Notes(context.Settings<OmpSettings>());
            _ = Load();

            async Task Load()
            {
                try
                {
                    var status = await context.RequestAsync(OmpContract.Status, new OmpNoParams());
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (current != generation) return;
                        version.Text = status.Installed ? "v" + status.Version : null;
                        missing.IsVisible = !status.Installed;
                        missingText.Text = status.Installed ? null : "omp is not installed. " + status.Detail;
                    });
                }
                catch (Exception) { }
                try
                {
                    var snapshot = await context.RequestAsync(OmpContract.Context, new OmpWorkspaceParams(workspaceId));
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (current != generation) return;
                        error.IsVisible = false;
                        ShowContext(snapshot);
                    });
                }
                catch (Exception failure)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (current != generation) return;
                        error.Text = "Couldn't read omp's context files: " + failure.Message;
                        error.IsVisible = true;
                    });
                }
            }
        }

        refresh.Click += (_, _) => Refresh();
        IDisposable? subscription = null;
        root.AttachedToVisualTree += (_, _) =>
        {
            subscription ??= context.OnSettings<OmpSettings>(_ => Refresh());
            Refresh();
        };
        root.DetachedFromVisualTree += (_, _) =>
        {
            subscription?.Dispose();
            subscription = null;
        };
        return root;
    }
}