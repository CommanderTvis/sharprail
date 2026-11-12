using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Processes.UI;

internal sealed partial class ProcessesSettingsView : StackPanel
{
    internal ProcessesSettingsView(IPluginUIContext context)
    {
        AvaloniaXamlLoader.Load(this);
        var stopChildren = this.FindControl<CheckBox>("ProcessesStopAgentChildren")!;
        stopChildren.Click += (_, _) => _ = UpdateAsync(stopChildren.IsChecked == true);
        async Task UpdateAsync(bool value)
        {
            try { await context.UpdateSettingsAsync(context.Settings<ProcessesSettings>() with { StopAgentChildren = value }); }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't change the Processes setting"); }
        }

        // Settings arrive with the host's snapshot; the check box updates in place.
        void Render(ProcessesSettings settings) => stopChildren.IsChecked = settings.StopAgentChildren;
        Render(context.Settings<ProcessesSettings>());
        IDisposable? subscription = null;
        AttachedToVisualTree += (_, _) =>
        {
            Render(context.Settings<ProcessesSettings>());
            subscription ??= context.OnSettings<ProcessesSettings>(Render);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            subscription?.Dispose();
            subscription = null;
        };
    }
}