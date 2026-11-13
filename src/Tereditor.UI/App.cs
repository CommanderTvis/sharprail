using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Client;
using Tereditor.Host.Core;
using Tereditor.Plugins.Tailscale;
using Tereditor.Plugins.Tailscale.UI;
using Tereditor.UI.Rendering;
using Tereditor.UI.State;
using Tereditor.UI.Terminal;

namespace Tereditor.UI;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        StartupPhases.Mark("app-initialize-start");
        AvaloniaXamlLoader.Load(this);
#if !ANDROID && !BROWSER
        if (OperatingSystem.IsMacOS()) ApplicationMenu.SetApplicationMenu(this);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.Args?.Contains("--tailscale", StringComparer.Ordinal) != true)
            PrepareStartup();
#endif
        // The kit has no theme of its own; the catalogue's default applies until a window resolves the host's.
        var theme = Themes.Resolve(Themes.DefaultId);
        if (ReferenceEquals(Ui.Theme, theme)) Ui.ApplyResources(this);
        else Ui.Apply(theme);
        ControlStates.Apply(Resources);
        // One style per typography role, so a control takes its type by class name.
        Styles.Add(Typography.Styles());
        Resources["TreeChevronMask"] = ((Border)Ui.Icon("arrowRight")).OpacityMask;
        foreach (var state in new[] { "Selected", "SelectedPointerOver", "SelectedPressed" })
            Resources["TreeViewItemBackground" + state] = Ui.Selected;
        // The reference's 20px chevron slot. Fluent's template sets its 12px margins directly, which outranks styles.
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<TreeViewItem>((_, e) =>
        {
            if (e.NameScope.Find<Panel>("PART_ExpandCollapseChevronContainer") is { } chevron) chevron.Margin = new Thickness(2, 0);
        });
        // A row with an icon reserves the icon column on its own, so the first pass already sizes the popup around the icon instead
        // of waiting for the shared column to settle (an unsettled column leaves icons outside the menu).
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<MenuItem>((item, e) =>
        {
            ReserveIconColumn(item);
            // Fluent's icon-to-text margin is wider than the row's left inset; one gap on both sides keeps the icon centred between edge and text.
            if (e.NameScope.Find<Control>("PART_IconPresenter") is { } icon) FitIconSlot(icon);
            if (e.NameScope.Find<Avalonia.Controls.Primitives.Popup>("PART_Popup") is { } popup)
                item.SubmenuOpened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => SnapSubmenu(item, popup), Avalonia.Threading.DispatcherPriority.Loaded);
        });
        MenuItem.IconProperty.Changed.AddClassHandler<MenuItem>((item, change) =>
        {
            ReserveIconColumn(item);
            // Whatever control a menu hands over is centred in the slot and never clips itself.
            if (change.NewValue is Control icon) { icon.HorizontalAlignment = HorizontalAlignment.Center; icon.VerticalAlignment = VerticalAlignment.Center; icon.ClipToBounds = false; }
        });
        // No popup leaves its window: the positioner slides and flips against the screen, which lets a menu opened near the window's
        // edge hang outside it, so every opened popup (menu, submenu, flyout, dropdown, autocomplete; not tooltips) is kept inside.
        Avalonia.Controls.Primitives.Popup.IsOpenProperty.Changed.AddClassHandler<Avalonia.Controls.Primitives.Popup>((popup, change) =>
        {
            if (change.NewValue is true) Avalonia.Threading.Dispatcher.UIThread.Post(() => KeepPopupInWindow(popup, 3), Avalonia.Threading.DispatcherPriority.Loaded);
            else if (PopupShifts.TryGetValue(popup, out var shift))
            {
                popup.HorizontalOffset -= shift.Value.X;
                popup.VerticalOffset -= shift.Value.Y;
                PopupShifts.Remove(popup);
            }
        });
        // Fluent's menu rows share icon, shortcut and chevron columns, which only settle after the first layout pass. A native
        // popup sized from that pass is narrower than its rows (clipping the icons and chevrons of mixed menus), so measure again.
        MenuBase.OpenedEvent.AddClassHandler<ContextMenu>((menu, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(menu.InvalidateMeasure, Avalonia.Threading.DispatcherPriority.Render));
        // A native popup keeps the frame it is born with, so the menu's width must already be the settled one when it opens:
        // settle the shared columns synchronously as the menu enters its popup (a desktop popup window lays out and takes its frame before Opened;
        // Opening runs while the menu is detached and measures 0), again at Opened and whenever rows arrive or change their icon, and pin it as MinWidth.
        Control.ContextMenuProperty.Changed.AddClassHandler<Control>((_, change) =>
        {
            if (change.NewValue is ContextMenu menu && SizedMenus.TryAdd(menu, menu))
            {
                menu.AttachedToVisualTree += (_, _) => SettleMenuWidth(menu);
                menu.Opened += (_, _) => SettleMenuWidth(menu);
                if (menu.Items is System.Collections.Specialized.INotifyCollectionChanged items) items.CollectionChanged += (_, _) => SettleMenuWidth(menu);
            }
        });
        MenuItem.IconProperty.Changed.AddClassHandler<MenuItem>((item, _) =>
        {
            if (item.Parent is ContextMenu menu) SettleMenuWidth(menu);
        });
        // A window does not inherit its owner's theme variant, and Fluent colours every stock control from it: a dialog,
        // Settings or plugin window would otherwise follow the OS appearance instead of the theme. Workbench windows
        // pass theirs on to owned windows when the theme changes.
        Window.OwnerProperty.Changed.AddClassHandler<Window>((window, _) =>
        {
            if (window.Owner is Window owner) window.RequestedThemeVariant = owner.RequestedThemeVariant;
        });
        StartupPhases.Mark("app-initialize-end");
    }

    // Creates terminal tabs for every window of this app, all attached to the app's one terminal host.
    public TerminalFactory Terminals { get; private set; } = launch => TerminalBackends.Unavailable("The app has not started.");

    public override void OnFrameworkInitializationCompleted()
    {
        StartupPhases.Mark("framework-ready");
#if !ANDROID && !BROWSER
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.Args?.Contains("--tailscale", StringComparer.Ordinal) == true)
            {
                var directory = TailscaleClientDirectory();
                var connect = new TailscaleDesktopWindow(Path.Combine(directory, "tailscale-client.json"));
                desktop.MainWindow = connect;
                connect.Connected += connection =>
                {
                    desktop.MainWindow = null;
                    StartDesktop(desktop, connection);
                    desktop.MainWindow?.Show();
                    connect.Close();
                };
            }
            else StartDesktop(desktop, null);
            IdleMemoryTrim.Start();
            ScrollTooltipGate.Start();
            UiLagWatchdog.Start();
        }
#endif
        base.OnFrameworkInitializationCompleted();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ContextMenu, ContextMenu> SizedMenus = [];

    // Measures the menu with no width limit until its desired width stops changing (the shared icon, shortcut and chevron
    // columns need a second pass), then makes that the minimum width so the popup frame is big enough from the start.
    internal static void SettleMenuWidth(ContextMenu menu)
    {
        menu.MinWidth = 0;
        // Settle under the width the menu may actually take, so a menu its style caps (or the window narrows) wraps at the cap.
        // A menu is in a visual tree only while its popup shows it, from just before the popup's first layout pass.
        var top = TopLevel.GetTopLevel(menu);
        if (top is null) return;
        // On desktop that top level is the menu's own popup window, whose size is the menu's, not room it may take: cap at the window that owns it.
        while (top is PopupRoot { ParentTopLevel: { } outer }) top = outer;
        var limit = Math.Min(menu.MaxWidth, Math.Max(0, top.ClientSize.Width - 16));
        var width = -1.0;
        for (var pass = 0; pass < 6; pass++)
        {
            // A parent's invalidation leaves its children's cached measures alone, so the rows would keep the width they had
            // before the shared shortcut column grew: invalidate every row part too.
            menu.InvalidateMeasure();
            foreach (var part in menu.GetVisualDescendants().OfType<Avalonia.Layout.Layoutable>()) part.InvalidateMeasure();
            menu.Measure(new Size(limit, double.PositiveInfinity));
            ShareColumns(menu);
            if (Math.Abs(menu.DesiredSize.Width - width) < 0.01) break;
            width = menu.DesiredSize.Width;
        }
        menu.MinWidth = Math.Min(Math.Ceiling(menu.DesiredSize.Width), limit);
    }

    // Fluent reconciles a menu's shared toggle, icon, shortcut and chevron columns only as a layout pass ends, and a desktop popup window takes
    // its frame from its first pass, before that: give each shared column of every row the widest content that column has in any row.
    private static void ShareColumns(ContextMenu menu)
    {
        var grids = menu.Items.OfType<MenuItem>()
            .Select(item => item.GetVisualDescendants().OfType<Grid>().FirstOrDefault(grid => grid.ColumnDefinitions.Any(column => column.SharedSizeGroup is not null)))
            .OfType<Grid>().ToArray();
        var widths = new Dictionary<string, double>();
        foreach (var grid in grids)
            for (var index = 0; index < grid.ColumnDefinitions.Count; index++)
                if (grid.ColumnDefinitions[index].SharedSizeGroup is { } group)
                    widths[group] = Math.Max(widths.GetValueOrDefault(group), grid.Children.Where(part => Grid.GetColumn(part) == index && Grid.GetColumnSpan(part) == 1).Select(part => part.DesiredSize.Width).DefaultIfEmpty(0).Max());
        foreach (var grid in grids)
            foreach (var column in grid.ColumnDefinitions)
                if (column.SharedSizeGroup is { } group && Math.Abs(column.MinWidth - widths[group]) > 0.01) column.MinWidth = widths[group];
    }

    // The distance from the menu's visible edge to a row's icon and from the icon to the row's text.
    internal const double IconGap = 10;

    // The submenu popup is placed against the row, which sits inside the parent's padding and border, and its native frame can add
    // room of its own: shift it so the submenu's visible edge touches the parent menu's visible edge, as a native menu does.
    private static void SnapSubmenu(MenuItem item, Avalonia.Controls.Primitives.Popup popup)
    {
        // Only a real, open submenu popup of a row that has a submenu: anything else (and a submenu the positioner flipped or slid
        // far from its parent) keeps the placement it was given.
        if (!item.HasSubMenu || !item.IsSubMenuOpen || !popup.IsOpen || popup.Child is not Visual child || TopLevel.GetTopLevel(item) is not { } top) return;
        var surface = item.GetVisualAncestors().TakeWhile(visual => visual != top).LastOrDefault() ?? item;
        if (TopLevel.GetTopLevel(child) is not { } childTop || surface.TranslatePoint(default, top) is not { } origin
            || child.TranslatePoint(default, childTop) is not { } childOrigin) return;
        var parentRight = top.PointToScreen(new Point(origin.X + surface.Bounds.Width, 0)).X;
        var childLeft = childTop.PointToScreen(childOrigin).X;
        // The window clamp's own shift is not part of the gap to correct.
        var kept = PopupShifts.TryGetValue(popup, out var shift) ? shift.Value.X : 0;
        var gap = (childLeft - parentRight) / top.RenderScaling - kept;
        if (Math.Abs(gap) > 0.5 && Math.Abs(gap) <= SnapReach) popup.HorizontalOffset -= gap;
        KeepPopupInWindow(popup, 2);
    }

    // The most a submenu may be off its parent's edge and still be snapped to it.
    private const double SnapReach = 24;

    // What the window clamp shifted each popup by, so closing the popup gives back the offset it was opened with.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Avalonia.Controls.Primitives.Popup, System.Runtime.CompilerServices.StrongBox<Vector>> PopupShifts = [];

    // Moves an open popup by the least it takes to lie inside the window that owns it (or aligns it to the window's top left when it is bigger).
    // Positions are compared on screen, so native popup windows and overlay popups are treated alike.
    private static void KeepPopupInWindow(Avalonia.Controls.Primitives.Popup popup, int attempts)
    {
        if (!popup.IsOpen || popup.Child is not Visual child || popup.Child is ToolTip || popup.Parent is ToolTip) return;
        var owner = TopLevel.GetTopLevel(popup.PlacementTarget) ?? TopLevel.GetTopLevel(popup) ?? (popup.Parent is Visual parent ? TopLevel.GetTopLevel(parent) : null);
        while (owner is PopupRoot root && root.ParentTopLevel is { } outer) owner = outer;
        if (owner is null or PopupRoot || TopLevel.GetTopLevel(child) is not { } host || child.Bounds.Width <= 0 || child.Bounds.Height <= 0
            || child.TranslatePoint(default, host) is not { } local) return;
        var origin = host.PointToScreen(local);
        var bounds = owner.PointToScreen(default);
        var dx = Shift(origin.X, origin.X + child.Bounds.Width * host.RenderScaling, bounds.X, bounds.X + owner.ClientSize.Width * owner.RenderScaling) / owner.RenderScaling;
        var dy = Shift(origin.Y, origin.Y + child.Bounds.Height * host.RenderScaling, bounds.Y, bounds.Y + owner.ClientSize.Height * owner.RenderScaling) / owner.RenderScaling;
        if (dx == 0 && dy == 0) return;
        popup.HorizontalOffset += dx;
        popup.VerticalOffset += dy;
        var total = PopupShifts.GetOrCreateValue(popup);
        total.Value += new Vector(dx, dy);
        if (attempts > 1) Avalonia.Threading.Dispatcher.UIThread.Post(() => KeepPopupInWindow(popup, attempts - 1), Avalonia.Threading.DispatcherPriority.Background);
    }

    // How far [low, high] must move to lie inside [min, max]; a span wider than the room starts at min. Whole pixels below one are ignored.
    private static double Shift(double low, double high, double min, double max)
    {
        var shift = high - low >= max - min ? min - low : low < min ? min - low : high > max ? max - high : 0;
        return Math.Abs(shift) < 1 ? 0 : shift;
    }

    // Every menu icon sits in one MenuIconSlot box, centred both ways and unclipped: Fluent's presenter aligns its content to the start, which
    // leaves a glyph narrower than the slot off-centre, so the content alignment is set once here for every menu.
    private static void FitIconSlot(Control slot)
    {
        slot.Margin = new Thickness(0, 0, IconGap, 0);
        slot.Width = Tereditor.Plugins.UI.Kit.LayoutRoles.MenuIconSlot;
        slot.Height = Tereditor.Plugins.UI.Kit.LayoutRoles.MenuIconSlot;
        slot.ClipToBounds = false;
        if (slot is ContentControl content) { content.HorizontalContentAlignment = HorizontalAlignment.Center; content.VerticalContentAlignment = VerticalAlignment.Center; }
        else if (slot is ContentPresenter presenter) { presenter.HorizontalContentAlignment = HorizontalAlignment.Center; presenter.VerticalContentAlignment = VerticalAlignment.Center; }
    }

    // The icon presenter is MenuIconSlot wide plus the icon gap: the room a row's icon needs in the icon column from the first pass.
    private static void ReserveIconColumn(MenuItem item)
    {
        var column = item.GetVisualDescendants().OfType<Grid>().SelectMany(grid => grid.ColumnDefinitions).FirstOrDefault(definition => definition.SharedSizeGroup == "MenuItemIcon");
        if (column is not null) column.MinWidth = item.Icon is null ? 0 : Tereditor.Plugins.UI.Kit.LayoutRoles.MenuIconSlot + IconGap;
    }

#if !ANDROID && !BROWSER
    private static string TailscaleClientDirectory() => Path.Combine(Environment.GetEnvironmentVariable("TEREDITOR_PROFILE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tereditor"), "tailscale-client");

    private static Task<DesktopStartup>? startup;

    internal static void PrepareStartup()
    {
        if (startup is not null) return;
        // Legacy profile migration can derive a theme pair. Prime the asset-backed catalogue on the
        // dispatcher before starting that worker; Avalonia's assembly asset cache is not concurrent.
        _ = Themes.All;
        startup = Task.Run(() => PrepareDesktop(null));
    }

    private sealed record DesktopStartup(string? Endpoint, string Token, string InitialRoot, ProfileStore Profile, HostStateStore Local, bool Remote,
        HostConnection Connection, IHostStateService StateService, RemoteTerminalAdapter? RemoteTerminals, PtyTerminalService? LocalTerminals,
        Tereditor.Host.Remote.LoopbackServer? Loopback, Tereditor.Host.Core.Plugins.PluginRuntime? Runtime, Tereditor.Host.Remote.HostListener? Listener,
        RemotePluginAdapter? RemotePlugins, IPluginService Plugins, Func<IProjectServices> Sessions, RemoteTerminalCatalogAdapter? RemoteCatalog, ITerminalCatalogService? Catalog);

    private static DesktopStartup PrepareDesktop(TailscaleConnection? chosen)
    {
        StartupPhases.Mark("desktop-prepare-start");
        var endpoint = chosen?.Address ?? Environment.GetEnvironmentVariable("TEREDITOR_REMOTE");
        var initialRoot = Environment.GetEnvironmentVariable("TEREDITOR_ROOT") ?? (chosen is null ? Directory.GetCurrentDirectory() : "");
        var profileDirectory = chosen is null ? Environment.GetEnvironmentVariable("TEREDITOR_PROFILE") : TailscaleClientDirectory();
        var profile = profileDirectory is null ? ProfileStore.OpenDefault(initialRoot) : new ProfileStore(profileDirectory);
        var token = chosen?.Token ?? Environment.GetEnvironmentVariable("TEREDITOR_TOKEN") ?? "";
        // The local host's state always migrates, so a remote session never leaves pre-host fields behind.
        var local = profile.OpenState();
        var remote = !string.IsNullOrEmpty(endpoint);
        // Every window's project session is the same client of the host as the state subscription.
        var connection = new HostConnection();
        IHostStateService stateService = remote ? new RemoteStateAdapter(new Uri(endpoint!), token, connection) : new LocalStateAdapter(local);
        var remoteTerminals = remote ? new RemoteTerminalAdapter(new Uri(endpoint!), token) : null;
        // Local sessions belong to the app's host: they survive their windows and end when the app quits.
        var localTerminals = remoteTerminals is null ? new PtyTerminalService(recordingsDirectory: Path.Combine(profile.DirectoryPath, "terminals"),
            replayBytes: () => local.Current.Settings.TerminalReplayKb * 1024, keepAfterExit: () => local.Current.Settings.TerminalKeepAfterExit,
            undoCloseSeconds: () => local.Current.Settings.TerminalUndoCloseSeconds,
            shellIntegration: () => local.Current.Settings.ShellIntegrationEnabled, terminalMemory: () => local.Current.Settings.TerminalMemory) : null;
        // A removed workspace takes its shells with it, whichever window removed it.
        if (localTerminals is not null) local.WorkspaceRemoved += path => _ = Task.Run(async () => await localTerminals.CloseWorkspaceAsync(path));
        local.Terminals = localTerminals;
        // The app's own host runs its plugins in process; a remote host runs them where it runs.
        var loopback = remote ? null : new Tereditor.Host.Remote.LoopbackServer(localTerminals, local);
        var tailscale = new TailscaleServing();
        var runtime = loopback is null ? null : new Tereditor.Host.Core.Plugins.PluginRuntime(new()
        {
            StateDirectory = profile.DirectoryPath,
            State = local,
            PublicBaseUrl = () => loopback.BaseUrl,
            Terminals = localTerminals,
            Builtins = Tereditor.Host.Core.Plugins.BuiltinPlugins.Create(tailscale)
        });
        if (loopback is not null) loopback.Plugins = runtime;
        var listener = runtime is null ? null : new Tereditor.Host.Remote.HostListener(initialRoot, local, localTerminals, runtime);
        listener?.ConfigureTailscale(tailscale);
        runtime?.Start();
        var remotePlugins = remote ? new RemotePluginAdapter(new Uri(endpoint!), token) : null;
        IPluginService plugins = remotePlugins ?? (IPluginService)new LocalPluginAdapter(runtime!);
        Func<IProjectServices> sessions = remote
            ? () => new RemoteProjectAdapter(new Uri(endpoint!), token, connection)
            : () => new LocalProjectAdapter(new ProjectServices(initialRoot, local, runtime!.AllowsExternalFile, runtime!.RepoHostFor, runtime!.NotifyBranchStatus));
        // Which terminal tabs exist is the host's to say, so every window and every other client shows the same ones.
        var remoteCatalog = remote ? new RemoteTerminalCatalogAdapter(new Uri(endpoint!), token) : null;
        ITerminalCatalogService? catalog = remoteCatalog is not null ? remoteCatalog : localTerminals is not null ? new LocalTerminalCatalogAdapter(localTerminals) : null;
        StartupPhases.Mark("desktop-prepare-end");
        return new(endpoint, token, initialRoot, profile, local, remote, connection, stateService, remoteTerminals, localTerminals, loopback, runtime, listener, remotePlugins, plugins, sessions, remoteCatalog, catalog);
    }

    private void StartDesktop(IClassicDesktopStyleApplicationLifetime desktop, TailscaleConnection? chosen)
    {
        var prepared = chosen is null && startup is not null ? startup.GetAwaiter().GetResult() : PrepareDesktop(chosen);
        var (endpoint, token, initialRoot, profile, local, remote, connection, stateService, remoteTerminals, localTerminals, loopback, runtime, listener, remotePlugins, plugins, sessions, remoteCatalog, catalog) = prepared;
        var state = new SharedState(stateService, profile.Data.Preferences, remote ? null : local.Current, connection);
        string Renderer() => profile.Data.Preferences.TerminalRenderer;
        Terminals = remoteTerminals is not null
            ? TerminalBackends.Ghostty(new RemoteTerminalConnection(new Uri(endpoint!), token, remoteTerminals), Renderer)
            : localTerminals is not null ? TerminalBackends.Ghostty(new LoopbackTerminals(new LocalTerminalAdapter(localTerminals), loopback!), Renderer)
            : launch => TerminalBackends.Unavailable("Embedded terminals currently require macOS.");
        var workbench = new Workbench(profile, state, Terminals, remote, sessions, plugins, catalog)
        {
            Endpoint = remote ? endpoint! : "local",
            Listener = listener,
            Notifier = OperatingSystem.IsMacOS() ? Notifications.MacNotifier.Create() : OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() ? new Notifications.LinuxNotifier() : null
        };
        StartupPhases.Mark("app-services-created");
        if (OperatingSystem.IsMacOS()) ApplicationMenu.Install(this, workbench.Commands);
        SecureInput.Shared.Automatic = profile.Data.Preferences.SecureInputForPasswords;
        SecureInputLifetime.Install(desktop, SecureInput.Shared);
        foreach (var slot in profile.Data.Windows.ToArray())
        {
            var root = slot.LastProject.Length > 0 ? slot.LastProject : slot == profile.Data.Windows[0] ? initialRoot : "";
            var window = workbench.Open(slot, root);
            // `--link <location>` opens a serialized location in the first window once it has restored its own.
            if (desktop.MainWindow is null && desktop.Args is { } args && Array.IndexOf(args, "--link") is >= 0 and var at && at + 1 < args.Length)
                window.StartLink = args[at + 1];
            desktop.MainWindow ??= window;
            if (window != desktop.MainWindow) window.Show();
        }
        workbench.Commands.Shutdown = () => desktop.TryShutdown();
        desktop.ShutdownRequested += (_, _) => workbench.ShuttingDown = true;
        desktop.Exit += (_, _) =>
        {
            (stateService as IDisposable)?.Dispose(); remoteTerminals?.Dispose(); remoteCatalog?.Dispose(); remotePlugins?.Dispose();
            // Off the UI thread: ending shells awaits their exit, which a blocked dispatcher would never resume.
            // Plugins stop first, before the terminals and the loopback server they reach.
            Task.Run(async () =>
            {
                if (listener is not null) await listener.DisposeAsync();
                if (runtime is not null) await runtime.DisposeAsync();
                if (loopback is not null) await loopback.DisposeAsync();
                if (localTerminals is not null) await localTerminals.DisposeAsync();
                if (local is not null) await local.StopLanguagesAsync();
            }).GetAwaiter().GetResult();
        };
    }
#endif
}