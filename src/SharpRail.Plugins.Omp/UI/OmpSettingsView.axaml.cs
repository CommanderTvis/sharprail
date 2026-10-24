using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Omp.UI;

/// <summary>Settings › omp: the command, the documented launch flags with their suggested values, the MCP profile and code intelligence.</summary>
internal sealed partial class OmpSettingsView : StackPanel
{
    internal OmpSettingsView(IPluginUIContext context)
    {
        AvaloniaXamlLoader.Load(this);
        var applying = false;
        void Update(Func<OmpSettings, OmpSettings> change) => _ = UpdateAsync(change);
        async Task UpdateAsync(Func<OmpSettings, OmpSettings> change)
        {
            try { await context.UpdateSettingsAsync(change(context.Settings<OmpSettings>())); }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't change the omp setting"); }
        }

        var command = this.FindControl<TextBox>("OmpCommandInput")!;
        var model = this.FindControl<AutoCompleteBox>("OmpModel")!;
        var approval = this.FindControl<AutoCompleteBox>("OmpApprovalMode")!;
        var advisor = this.FindControl<CheckBox>("OmpAdvisor")!;
        var mcp = this.FindControl<CheckBox>("OmpMcp")!;
        var ownLsp = this.FindControl<CheckBox>("OmpOwnLsp")!;
        model.ItemsSource = OmpLaunch.ModelSuggestions;
        approval.ItemsSource = OmpLaunch.ApprovalModes;

        void Text(Control input, Func<string?> read, Func<OmpSettings, string> current, Func<OmpSettings, string, OmpSettings> write)
        {
            void Save()
            {
                var value = read()?.Trim() ?? "";
                if (!applying && value != current(context.Settings<OmpSettings>())) Update(settings => write(settings, value));
            }
            input.LostFocus += (_, _) => Save();
            input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Save(); e.Handled = true; } };
        }
        Text(command, () => command.Text is { Length: > 0 } typed && typed.Trim().Length > 0 ? typed : "omp", settings => settings.Command, (settings, value) => settings with { Command = value });
        Text(model, () => model.Text, settings => settings.Model, (settings, value) => settings with { Model = value });
        Text(approval, () => approval.Text, settings => settings.ApprovalMode, (settings, value) => settings with { ApprovalMode = value });
        model.DropDownClosed += (_, _) => { if (!applying && (model.Text ?? "") != context.Settings<OmpSettings>().Model) Update(settings => settings with { Model = model.Text?.Trim() ?? "" }); };
        approval.DropDownClosed += (_, _) => { if (!applying && (approval.Text ?? "") != context.Settings<OmpSettings>().ApprovalMode) Update(settings => settings with { ApprovalMode = approval.Text?.Trim() ?? "" }); };
        advisor.Click += (_, _) => Update(settings => settings with { Advisor = advisor.IsChecked == true });
        mcp.Click += (_, _) => Update(settings => settings with { Mcp = mcp.IsChecked == true });
        ownLsp.Click += (_, _) => Update(settings => settings with { OwnCodeIntelligence = ownLsp.IsChecked == true });

        void Render(OmpSettings settings)
        {
            applying = true;
            if (!command.IsFocused) command.Text = settings.Command;
            if (!model.IsKeyboardFocusWithin) model.Text = settings.Model;
            if (!approval.IsKeyboardFocusWithin) approval.Text = settings.ApprovalMode;
            advisor.IsChecked = settings.Advisor;
            mcp.IsChecked = settings.Mcp;
            ownLsp.IsChecked = settings.OwnCodeIntelligence;
            applying = false;
        }

        Render(context.Settings<OmpSettings>());
        IDisposable? subscription = null;
        AttachedToVisualTree += (_, _) =>
        {
            Render(context.Settings<OmpSettings>());
            subscription ??= context.OnSettings<OmpSettings>(Render);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            subscription?.Dispose();
            subscription = null;
        };
    }
}