using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>A plugin uninstall the user asked for, at the scope that declares it.</summary>
internal sealed record PluginUninstallTarget(string Name, ClaudeWritableScope Scope);

/// <summary>A plugin move between scopes.</summary>
internal sealed record PluginMoveTarget(string Name, ClaudeWritableScope From, ClaudeWritableScope To);

/// <summary>
/// Every write the pane makes is approved first: a configuration edit as a diff of the file it lands in, named in words
/// rather than a path, and a Claude CLI run as the exact argv the host will execute.
/// </summary>
internal static class ReviewDialogs
{
    private static string ScopeLabel(ClaudeWritableScope scope) => scope switch
    {
        ClaudeWritableScope.User => "Your settings",
        ClaudeWritableScope.Project => "Project settings",
        _ => "Project settings, private"
    };

    /// <summary>
    /// Approves one edit. The chosen scope and its plan live only in this dialog, so a plan can never be applied to
    /// another edit; changing the scope discards the previous plan. Completes true when the edit was written.
    /// </summary>
    public static async Task<bool> EditAsync(IPluginUIContext context, Window owner, string workspaceId, string? tabKey, PendingEdit pending)
    {
        var window = DialogWindow.Create(pending.Title, 704);
        window.Tag = "ClaudeEditDialog";
        var panel = window.FindControl<StackPanel>("DialogFields")!;
        var actions = window.FindControl<StackPanel>("DialogActions")!;
        ClaudeWritableScope? scope = null;
        ClaudeEditPlan? plan = null;
        var applied = false;
        var busy = false;
        var generation = 0;

        panel.Children.Add(Ui.Text("Where should this change go?", Ui.Muted, 12));
        var scopes = new StackPanel { Spacing = 4 };
        panel.Children.Add(scopes);
        var error = ClaudeParts.Wrapped("", Ui.Danger, 13);
        error.Name = "ClaudeEditError";
        error.IsVisible = false;
        panel.Children.Add(error);
        var planArea = new StackPanel { Spacing = 4 };
        panel.Children.Add(planArea);

        var apply = ClaudeParts.Primary(Ui.Button("Apply this change", () => { }));
        apply.Name = "ClaudeEditApply";
        apply.IsEnabled = false;

        void ShowError(string? message)
        {
            error.Text = message ?? "";
            error.IsVisible = message is not null;
        }

        void RenderScopes()
        {
            scopes.Children.Clear();
            foreach (var candidate in ClaudeValues.EditScopes(pending.Edit))
            {
                var content = new StackPanel { Spacing = 2 };
                content.Children.Add(Ui.Text(ScopeLabel(candidate), scope == candidate ? Ui.TextBrush : Ui.Muted, 13));
                content.Children.Add(Ui.Text("Affects " + ClaudeValues.ScopeWording(candidate), Ui.Hint, 12));
                var button = new Button
                {
                    Name = "ClaudeEditScope_" + candidate.Name(),
                    Tag = scope == candidate,
                    Content = content,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 8),
                    Background = scope == candidate ? Ui.PrimarySubtle : Brushes.Transparent,
                    BorderBrush = scope == candidate ? Ui.PrimaryMuted : Ui.BorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4)
                };
                button.Click += (_, _) => _ = Choose(candidate);
                scopes.Children.Add(button);
            }
        }

        void RenderPlan()
        {
            planArea.Children.Clear();
            apply.IsEnabled = plan is { Changes: true } && !busy;
            if (plan is null) return;
            planArea.Children.Add(ClaudeParts.Wrapped(plan.Summary, Ui.TextBrush, 13));
            planArea.Children.Add(ClaudeParts.Code(ScopedSetting.AbbreviateHomePath(plan.Path) + (plan.Exists ? "" : " (will be created)")));
            foreach (var warning in plan.Warnings)
            {
                var line = new DockPanel { Name = "ClaudeEditWarning" };
                var icon = Ui.Icon("alertWarning", Ui.Warning, 14);
                icon.Margin = new Thickness(0, 0, 4, 0);
                DockPanel.SetDock(icon, Dock.Left);
                line.Children.Add(icon);
                line.Children.Add(ClaudeParts.Wrapped(warning, Ui.Warning));
                planArea.Children.Add(line);
            }
            if (!plan.Changes)
            {
                planArea.Children.Add(Ui.Text("That file already says this — nothing to change.", Ui.Muted, 13));
                return;
            }
            var diff = new StackPanel { Name = "ClaudeEditDiff" };
            foreach (var line in plan.Diff)
            {
                var (prefix, foreground, background) = line.Kind switch
                {
                    ClaudeDiffKind.Add => ("+ ", (IBrush)Ui.Success, (IBrush)Ui.SuccessWash),
                    ClaudeDiffKind.Remove => ("- ", Ui.Danger, Ui.DangerWash),
                    ClaudeDiffKind.Gap => ("", Ui.Hint, Ui.Elevated),
                    _ => ("  ", Ui.Muted, Brushes.Transparent)
                };
                var text = ClaudeParts.Code(line.Kind == ClaudeDiffKind.Gap ? $"⋯ {line.Text} ⋯" : prefix + line.Text, foreground);
                text.TextWrapping = TextWrapping.Wrap;
                text.TextTrimming = TextTrimming.None;
                if (line.Kind == ClaudeDiffKind.Gap) text.HorizontalAlignment = HorizontalAlignment.Center;
                diff.Children.Add(new Border { Tag = line.Kind, Background = background, Padding = new Thickness(8, 0), Child = text });
            }
            planArea.Children.Add(new Border
            {
                BorderBrush = Ui.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Background = Ui.Surface,
                Child = new ScrollViewer { MaxHeight = 360, Content = diff }
            });
        }

        async Task Choose(ClaudeWritableScope next)
        {
            if (next == scope) return;
            scope = next;
            plan = null;
            ShowError(null);
            RenderScopes();
            RenderPlan();
            var mine = ++generation;
            try
            {
                var planned = await context.RequestAsync(ClaudeCodeContract.PlanEdit, new ClaudeEditRequest(workspaceId, next, pending.Edit) { TabKey = tabKey });
                if (mine != generation) return;
                plan = planned;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                if (mine == generation) ShowError(ClaudeParts.ErrorText(failure));
            }
            RenderPlan();
        }

        apply.Click += async (_, _) =>
        {
            if (scope is not { } chosen || plan is not { } approved) return;
            busy = true;
            RenderPlan();
            try
            {
                await context.RequestAsync(ClaudeCodeContract.ApplyEdit, new ClaudeApplyRequest(workspaceId, chosen, pending.Edit, approved.BaseHash) { TabKey = tabKey });
                applied = true;
                window.Close();
            }
            catch (Exception failure) when (failure is not OperationCanceledException) { ShowError(ClaudeParts.ErrorText(failure)); }
            finally
            {
                busy = false;
                if (!applied) RenderPlan();
            }
        };
        actions.Children.Add(Ui.Button("Cancel", () => window.Close()));
        actions.Children.Add(apply);
        RenderScopes();
        await window.ShowDialog(owner);
        return applied;
    }

    // One dialog shape for every Claude CLI run: what it does, the composed argv, its failure, and Run.
    private static async Task<bool> CommandAsync(Window owner, string name, string title, string description, Control? compose,
        Func<Task<IReadOnlyList<IReadOnlyList<string>>?>> plan, Func<Task> run, string runLabel, string runningLabel, Action<Action>? recompose = null)
    {
        var window = DialogWindow.Create(title, 608);
        window.Tag = name + "Dialog";
        var explanation = window.FindControl<TextBlock>("DialogExplanation")!;
        explanation.Text = description;
        explanation.IsVisible = true;
        var panel = window.FindControl<StackPanel>("DialogFields")!;
        if (compose is not null) panel.Children.Add(compose);
        var command = ClaudeParts.Code("Composing…", Ui.TextBrush);
        command.Name = name + "Command";
        command.TextWrapping = TextWrapping.Wrap;
        command.TextTrimming = TextTrimming.None;
        panel.Children.Add(new Border { Background = Ui.Surface, CornerRadius = new CornerRadius(4), Padding = new Thickness(8), Child = command });
        var error = ClaudeParts.Wrapped("", Ui.Danger);
        error.Name = name + "Error";
        error.IsVisible = false;
        panel.Children.Add(error);
        var actions = window.FindControl<StackPanel>("DialogActions")!;
        var cancel = Ui.Button("Cancel", () => window.Close());
        var go = ClaudeParts.Primary(Ui.Button(runLabel, () => { }));
        go.Name = name + "Run";
        go.IsEnabled = false;
        var done = false;
        var generation = 0;
        async Task Compose()
        {
            var mine = ++generation;
            go.IsEnabled = false;
            command.Text = "Composing…";
            try
            {
                var argv = await plan();
                if (mine != generation) return;
                command.Text = argv is null ? "Composing…" : string.Join("\n", argv.Select(line => "$ " + string.Join(' ', line)));
                go.IsEnabled = argv is not null;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                if (mine != generation) return;
                error.Text = ClaudeParts.ErrorText(failure);
                error.IsVisible = true;
            }
        }
        go.Click += async (_, _) =>
        {
            go.IsEnabled = cancel.IsEnabled = false;
            go.Content = Ui.Text(runningLabel, Ui.OnPrimary);
            error.IsVisible = false;
            try
            {
                await run();
                done = true;
                window.Close();
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                error.Text = ClaudeParts.ErrorText(failure);
                error.IsVisible = true;
            }
            finally
            {
                if (!done)
                {
                    go.Content = Ui.Text(runLabel, Ui.OnPrimary);
                    go.IsEnabled = cancel.IsEnabled = true;
                }
            }
        };
        actions.Children.Add(cancel);
        actions.Children.Add(go);
        recompose?.Invoke(() => _ = Compose());
        _ = Compose();
        await window.ShowDialog(owner);
        return done;
    }

    public static Task<bool> UninstallAsync(IPluginUIContext context, Window owner, string workspaceId, string? tabKey, PluginUninstallTarget target) =>
        CommandAsync(owner, "ClaudeUninstall", $"Uninstall {target.Name}",
            $"Claude Code removes the plugin from your {target.Scope.Name()} settings, its install record and the files it downloaded. Reinstalling it means adding it from its marketplace again.",
            null,
            async () => [(await context.RequestAsync(ClaudeCodeContract.PluginUninstallPlan, new PluginUninstallPlanParams(workspaceId, target.Name, target.Scope))).Command],
            () => context.RequestAsync(ClaudeCodeContract.PluginUninstall, new PluginUninstallParams(workspaceId, target.Name, target.Scope) { TabKey = tabKey }).AsTask(),
            "Run it", "Uninstalling…");

    public static Task<bool> MoveAsync(IPluginUIContext context, Window owner, string workspaceId, string? tabKey, PluginMoveTarget target) =>
        CommandAsync(owner, "ClaudeMove", $"Move {target.Name} to {target.To.Name()}",
            $"The plugin becomes installed {ClaudeValues.PluginScopeWording(target.To)}, and the {target.From.Name()}-scope copy is removed. Two of Claude Code's own commands, in this order:",
            null,
            async () => (await context.RequestAsync(ClaudeCodeContract.PluginMovePlan, new PluginMovePlanParams(workspaceId, target.Name, target.From, target.To))).Commands,
            () => context.RequestAsync(ClaudeCodeContract.PluginMove, new PluginMoveParams(workspaceId, target.Name, target.From, target.To) { TabKey = tabKey }).AsTask(),
            "Run both", "Moving…");

    /// <summary>A <c>claude plugin marketplace</c> run; adding also composes the source and the scope.</summary>
    public static Task<bool> MarketplaceAsync(IPluginUIContext context, Window owner, string workspaceId, string? tabKey, ClaudeMarketplaceAction target)
    {
        var (title, description) = target switch
        {
            AddMarketplace => ("Add a marketplace", "Claude Code fetches the catalog and declares it in the chosen scope's settings."),
            RemoveMarketplace remove => ($"Remove {remove.Name}", "Claude Code removes the declaration; plugins installed from it stay until uninstalled."),
            UpdateMarketplace update => ($"Update {update.Name}", "Claude Code refreshes the catalog from its source."),
            _ => ("", "")
        };
        Control? compose = null;
        var source = ClaudeParts.Input("ClaudeMarketplaceSource", "anthropics/claude-plugins-official");
        var scope = ClaudeWritableScope.User;
        Action changed = () => { };
        if (target is AddMarketplace)
        {
            var scopes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            void RenderScopes()
            {
                scopes.Children.Clear();
                foreach (var candidate in ClaudeValues.WritableScopes)
                {
                    var segment = Ui.Segment("ClaudeMarketplaceScope_" + candidate.Name(), candidate.Name());
                    segment.IsChecked = scope == candidate;
                    segment.Click += (_, _) => { scope = candidate; RenderScopes(); changed(); };
                    scopes.Children.Add(segment);
                }
            }
            RenderScopes();
            var fields = new StackPanel { Spacing = 8 };
            fields.Children.Add(ClaudeParts.Field("URL, path, or GitHub owner/repo", source));
            fields.Children.Add(scopes);
            compose = fields;
        }
        ClaudeMarketplaceAction? Action() => target is AddMarketplace
            ? source.Text?.Trim() is { Length: > 0 } text ? new AddMarketplace(text, scope) : null
            : target;
        return CommandAsync(owner, "ClaudeMarketplace", title, description, compose,
            async () => Action() is { } action ? [(await context.RequestAsync(ClaudeCodeContract.MarketplacePlan, new MarketplacePlanParams(workspaceId, action))).Command] : null,
            () => context.RequestAsync(ClaudeCodeContract.MarketplaceRun, new MarketplaceRunParams(workspaceId, Action()!) { TabKey = tabKey }).AsTask(),
            "Run it", "Running…",
            recompose =>
            {
                changed = recompose;
                source.TextChanged += (_, _) => recompose();
            });
    }
}