using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;


namespace SharpRail.UI.Docking;

public sealed partial class TabSearch : UserControl
{
    public TabSearch() => AvaloniaXamlLoader.Load(this);

    internal TabSearch(Func<IReadOnlyList<DockTab>> tabs, Func<DockTab, string> icon, Action<DockTab> select, Action dismiss) : this()
    {
        var search = this.FindControl<TextBox>("TabOverflowSearch")!;
        var list = this.FindControl<ListBox>("TabOverflowResults")!;
        var empty = this.FindControl<TextBlock>("EmptyResults")!;
        this.FindControl<ContentControl>("SearchIcon")!.Content = Ui.Icon("search", size: 14);
        list.ItemTemplate = new FuncDataTemplate<DockTab>((tab, _) => tab is null ? null : Ui.Row(icon(tab), tab.Title, Ui.TextBrush));
        void Filter()
        {
            list.ItemsSource = tabs().Where(tab => (tab.Title + " " + tab.Path).Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
            list.SelectedIndex = list.ItemCount > 0 ? 0 : -1;
            list.IsVisible = list.ItemCount > 0;
            empty.IsVisible = list.ItemCount == 0;
        }
        void Activate()
        {
            if (list.SelectedItem is DockTab tab) select(tab);
        }
        search.TextChanged += (_, _) => Filter();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { dismiss(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Activate(); e.Handled = true; }
            else if (e.Key is Key.Down or Key.Up && list.ItemCount > 0)
            {
                list.SelectedIndex = (list.SelectedIndex + (e.Key == Key.Down ? 1 : -1) + list.ItemCount) % list.ItemCount;
                list.ScrollIntoView(list.SelectedItem!); e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        list.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) Activate();
        };
        AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => search.Focus());
        Filter();
    }
}