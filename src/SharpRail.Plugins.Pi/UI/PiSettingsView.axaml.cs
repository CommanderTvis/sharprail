using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Pi.UI;

/// <summary>Settings › Pi: the command and the model and thinking level a new chat starts with.</summary>
internal sealed partial class PiSettingsView : StackPanel
{
    internal PiSettingsView(IPluginUIContext context, Func<PiStatus?> status)
    {
        AvaloniaXamlLoader.Load(this);
        var applying = false;
        var command = this.FindControl<TextBox>("PiCommandInput")!;
        var model = this.FindControl<TextBox>("PiModel")!;
        var thinking = this.FindControl<TextBox>("PiThinking")!;
        var version = this.FindControl<TextBlock>("PiVersion")!;

        void Update(Func<PiSettings, PiSettings> change) => _ = UpdateAsync(change);
        async Task UpdateAsync(Func<PiSettings, PiSettings> change)
        {
            try { await context.UpdateSettingsAsync(change(context.Settings<PiSettings>())); }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't change the Pi setting"); }
        }
        void Text(TextBox input, Func<PiSettings, string> current, Func<PiSettings, string, PiSettings> write, string fallback = "")
        {
            void Save()
            {
                var value = input.Text?.Trim() is { Length: > 0 } typed ? typed : fallback;
                if (!applying && value != current(context.Settings<PiSettings>())) Update(settings => write(settings, value));
            }
            input.LostFocus += (_, _) => Save();
            input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Save(); e.Handled = true; } };
        }
        Text(command, settings => settings.Command, (settings, value) => settings with { Command = value }, "pi");
        Text(model, settings => settings.Model, (settings, value) => settings with { Model = value });
        Text(thinking, settings => settings.Thinking, (settings, value) => settings with { Thinking = value });

        void Render(PiSettings settings)
        {
            applying = true;
            if (!command.IsFocused) command.Text = settings.Command;
            if (!model.IsFocused) model.Text = settings.Model;
            if (!thinking.IsFocused) thinking.Text = settings.Thinking;
            version.Text = status() switch
            {
                { Installed: true, Version: { } found } => "Pi v" + found,
                { Installed: true } => "Pi is installed.",
                { Detail: { } detail } => "Pi is not installed: " + detail,
                _ => ""
            };
            applying = false;
        }
        Render(context.Settings<PiSettings>());
        IDisposable? subscription = null;
        AttachedToVisualTree += (_, _) =>
        {
            Render(context.Settings<PiSettings>());
            subscription ??= context.OnSettings<PiSettings>(Render);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            subscription?.Dispose();
            subscription = null;
        };
    }
}