using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Platform.Storage;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit.Editor;

namespace SharpRail.UI.Panels;

public sealed partial class SettingsWindow
{
    internal const string HighlightingPrompt = "Use the builtin add-highlighting skill to help me add custom syntax highlighting in SharpRail. Ask which language or file format and filename patterns I want, then prepare a TextMate JSON grammar for Settings > Highlighting.";

    private Control HighlightingSettings()
    {
        var panel = Page("HighlightingPage");
        var patterns = PageControl<TextBox>(panel, "HighlightingPatterns");
        var grammar = PageControl<TextBox>(panel, "HighlightingGrammar");
        var message = PageControl<TextBlock>(panel, "HighlightingError");
        var add = PageControl<Button>(panel, "HighlightingImport");
        var choose = PageControl<Button>(panel, "HighlightingChoose");
        void Failed(Exception failure) { message.Text = failure.Message; message.IsVisible = true; }
        choose.IsEnabled = StorageProvider.CanOpen;
        choose.Click += async (_, _) =>
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new()
                {
                    Title = "Choose a TextMate JSON grammar",
                    AllowMultiple = false,
                    FileTypeFilter = [new("TextMate JSON") { Patterns = ["*.json"] }]
                });
                if (files.Count == 0) return;
                var text = await Task.Run(async () =>
                {
                    await using var stream = await files[0].OpenReadAsync();
                    using var reader = new StreamReader(stream);
                    var buffer = new char[512 * 1024 + 1];
                    var count = await reader.ReadBlockAsync(buffer.AsMemory(), lifetime.Token);
                    if (count == buffer.Length) throw new ArgumentException("The grammar must be at most 512 Ki characters.");
                    return new string(buffer, 0, count);
                }, lifetime.Token);
                if (!lifetime.IsCancellationRequested) { grammar.Text = text; message.IsVisible = false; }
            }
            catch (OperationCanceledException) { }
            catch (Exception failure) { Failed(failure); }
        };
        add.Click += async (_, _) =>
        {
            add.IsEnabled = false;
            try
            {
                var text = grammar.Text ?? "";
                var filenames = patterns.Text ?? "";
                var definition = await Task.Run(() => CustomHighlighting.Import(text, filenames), lifetime.Token);
                var existing = state.Current.Settings.CustomHighlighting;
                var definitions = await Task.Run(() => CustomHighlighting.Read(existing)
                    .Where(item => item.Scope != definition.Scope).Append(definition).ToArray(), lifetime.Token);
                await SaveHighlighting(definitions);
                message.IsVisible = false;
            }
            catch (OperationCanceledException) { }
            catch (Exception failure) { Failed(failure); }
            finally { add.IsEnabled = true; }
        };
        PopulateHighlightingGrammars(panel);
        BindHighlightingAgent(PageControl<Button>(panel, "HighlightingAskClaude"), "claude");
        BindHighlightingAgent(PageControl<Button>(panel, "HighlightingAskCodex"), "codex");
        return panel;
    }

    private void PopulateHighlightingGrammars(Control panel)
    {
        var list = PageControl<StackPanel>(panel, "HighlightingGrammars");
        var message = PageControl<TextBlock>(panel, "HighlightingError");
        void Failed(Exception failure) { message.Text = failure.Message; message.IsVisible = true; }
        list.Children.Clear();
        var definitions = CustomHighlighting.Current;
        if (definitions.Length == 0) list.Children.Add(Ui.Text("No custom grammars added.", Ui.Muted));
        foreach (var definition in definitions)
        {
            var row = Page("HighlightingGrammarRow");
            PageControl<TextBlock>(row, "HighlightingGrammarLabel").Text = definition.Name + " · " + string.Join(", ", definition.Patterns);
            var remove = PageControl<Button>(row, "HighlightingRemove");
            remove.Click += async (_, _) =>
            {
                remove.IsEnabled = false;
                try
                {
                    var json = state.Current.Settings.CustomHighlighting;
                    var remaining = await Task.Run(() => CustomHighlighting.Read(json).Where(item => item.Scope != definition.Scope).ToArray(), lifetime.Token);
                    await SaveHighlighting(remaining);
                }
                catch (Exception failure) { Failed(failure); remove.IsEnabled = true; }
            };
            list.Children.Add(row);
        }
    }

    private Task SaveHighlighting(CustomGrammar[] definitions)
    {
        return Task.Run(async () =>
        {
            var json = JsonSerializer.Serialize(definitions);
            if (json.Length > 2 * 1024 * 1024) throw new ArgumentException("Custom highlighting exceeds the 2 Mi character limit. Remove a grammar first.");
            await state.ChangeAsync(HostStateChange.Setting("custom-highlighting", json));
        }, lifetime.Token);
    }

    private void BindHighlightingAgent(Button button, string id)
    {
        GateHighlightingAgent(button, id);
        button.Click += async (_, _) =>
        {
            try
            {
                var launcher = window.Workbench.PluginRegistry.LauncherList.FirstOrDefault(item => item.Id == id);
                if (launcher is null || !launcher.Availability().Available) return;
                var command = launcher.TerminalCommand(new LauncherCommandOptions { InitialPrompt = HighlightingPrompt });
                await window.OpenPluginTerminalAsync(window.WorkspaceRoot, new() { Command = command });
                Close();
            }
            catch (Exception failure) { error.Text = failure.Message; error.IsVisible = true; }
        };
    }

    private void GateHighlightingAgent(Button button, string id)
    {
        var launcher = window.Workbench.PluginRegistry.LauncherList.FirstOrDefault(item => item.Id == id);
        var availability = launcher?.Availability();
        button.IsEnabled = launcher is not null && availability?.Available == true && window.WorkspaceRoot.Length > 0;
        ToolTip.SetTip(button, button.IsEnabled ? null : availability?.Reason ?? "Enable this agent plugin and open a workspace first.");
    }

    private void RefreshHighlightingAgents()
    {
        if (body.Content is not Control panel) return;
        GateHighlightingAgent(PageControl<Button>(panel, "HighlightingAskClaude"), "claude");
        GateHighlightingAgent(PageControl<Button>(panel, "HighlightingAskCodex"), "codex");
    }
}