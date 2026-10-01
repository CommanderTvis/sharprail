# Panels: Settings and shared dialogs

Upstream: apps/web/src/panels/SPEC.md @ c44534ea

## Responsibility

The modal surfaces the workbench opens: the Settings window (`SettingsWindow.axaml` / `.cs`), the shared
dialog card (`DialogWindow.axaml` / `.cs`) with its factory `Dialogs`, the Start work dialog
(`NewWorkspaceDialog`), the workspace search dialog (`SearchDialog`), and the local GitHub CLI probe
(`GitHubStatus.cs`). Static layout, page templates and
styles live in compiled `.axaml`; C# fills content and wires host interaction. The workbench panels that open
these surfaces are specified in [../Panels.SPEC.md](../Panels.SPEC.md).

## Boundary

- Settings reads and changes shared settings only through `SharedState` / `IHostStateService` and re-renders
  when the snapshot broadcast arrives; it writes window-local and app-local preferences through
  `ProfileStore`. It never keeps a local copy of host state.
- Dialogs are presentational: they return the user's choice and the caller performs the operation.
- Forbidden: host project or Git calls from a dialog, and reaching into docking beyond the `LayoutSession`
  handed to Settings.

## Dialog card

- One borderless, transparent `DialogWindow` centred on its owner: an elevated card with an 8px radius,
  the strong border, 16px padding and gaps, and a large appearance-dependent shadow. The owner window dims
  behind it while it is open.
- Heading, optional explanation, optional fields and right-aligned actions. An empty field area is hidden so
  it adds no gap. Standard dialogs use a 448px card.
- Buttons are 32px tall; the confirming action is the solid primary (`primary` class: `PrimaryFill`,
  `PrimaryFillHover` on hover, `OnPrimary` label), others are outlined in the text colour.
- Escape closes as a dismissal. `Dialogs.Confirm` focuses Cancel initially; `Dialogs.Prompt` and
  `Dialogs.HostPath` focus their first field; `Dialogs.AskToSave` focuses Save.
- `Dialogs.HostPath` is the Open project by path dialog: for a remote host it says the path belongs to the
  computer running SharpRail (locally that computer is this one, so the copy just asks for a folder), shows the native picker's failure when there was one, and returns the trimmed path.

## Search

A query field over a results list. Typing searches after 150ms of quiet and cancels a superseded query;
results are grouped under their file, one row per matching line with its number and text. An empty result
says “No matches”, a truncated one says how many it shows, and a failed search shows the host's error.
Clicking a row closes the dialog with that hit; Escape closes it with none. Deliberately absent: regular
expressions, a case toggle, a glob filter and replace.

## Start work

- The title is the constant “Start work”; the dialog never renames itself under the user. A two-option
  target segment sits directly under it, both options always visible: New worktree or Project folder. The
  only mode-aware prose is one line below it: “A separate git worktree on its own new branch.” or “No
  isolation: work lands in your project folder's current checkout.” The submit reads Create or Start.
- The dialog always opens on the worktree side; there is no opener-chosen target.
- Worktree mode shows the project and a base-branch trigger reading “From {base}”. Its flyout is a searchable
  list grouped Local, then Remote with one subgroup per remote whose rows show the branch name without the
  remote; the full ref stays each row's identity and automation name. The host's default base is marked
  `default`. Enter in the search picks the first match, Escape returns to the trigger.
- Each remote subgroup's heading collapses and expands it; a collapsed remote's rows are not built, so search
  skips them. The choice is remembered per remote name in the profile (`CollapsedRemotes`), one preference
  for every branch picker rather than one per surface.
- The dialog opens from a cached branch catalogue and a fresh catalogue is prefetched in the background;
  when it lands it replaces the list and, unless the user already picked, the default base. The default base
  is whatever the host reports, never a literal `HEAD` sentinel that would be believed and persisted.
- Worktree mode has a Name field prefilled with the host's next free `workspace-N`, so the name is visible
  before creation and follows a prefetched catalogue while untouched. Only an edited name is sent: the
  workbench stores it as the new workspace's host label.
- Folder mode hides the base picker and the name, and says where the work lands with an “On {branch}” line
  read from the catalogue's checked-out branch (absent when HEAD is detached); submitting enters the
  project's Default workspace, creating nothing.
- When plugins register agent launchers (W9), a “Start in a terminal with” row offers Nothing and each launcher; an
  unavailable launcher is disabled with its reason as the tooltip. The chosen launcher's command is typed into a new
  terminal of the workspace once it opens.
- Worktree submit returns the choice, and the workbench creates the worktree with the host-suggested path and
  branch, persists the base as the workspace's comparison target, and opens it. A failed create reports the
  error and keeps the rail consistent.

## Settings

A modal two-pane window: a section rail (Appearance, Line width, Layout, Projects, GitHub, Plugins, then each
active plugin's own sections in registration order) and a scrolling
content pane. Escape and the close button dismiss. It first opens on Appearance and afterwards on the section it was last left on in that window, the way a
Preferences window returns where it was left; it is sized to 80% of the owner's height.
Shared-setting writes go to the host and the view converges on the broadcast; a rejection shows “The host
could not save this change: …” inline and re-renders from host state.

- Appearance explains that mode and pair are shared by every window and client while each device reads its
  own light or dark setting, then offers two top-level cards, Fixed (“Use one theme everywhere.”) and Match
  system. Fixed shows the theme list. Match system shows Light theme and Dark theme selectors filtered to
  that appearance plus a “Current on this device” row reading `<Light|Dark> → <palette> <theme>`; either slot
  may be normal or high contrast. First enable sends mode and the derived pair atomically; slot edits send the
  complete pair; returning to Fixed changes only the mode, keeping both choices. An unavailable or
  wrong-appearance configured theme is disclosed beside the fallback and never written back. The page
  re-renders when the device appearance changes. All catalogue, resolution and pair logic comes from
  `Rendering/Themes` (see Rendering/Themes.SPEC.md). The interface font size is an app preference below.
- Line width has File and Markdown groups, each a 40–240 integer field with a `symbols` suffix and an
  explicit Save, plus an independent “Limit lines to this width” switch. Defaults are 120 and 78, bounded.
  Invalid drafts stay local with a range error; Escape restores the host value; Enter saves when valid. A
  broadcast updates an untouched field and the switch in place, but keeps an edited draft.
- Layout lists Balanced, Focus, Review and the named custom presets. Custom save, rename and delete are shared
  host state; the default preset and side/bottom group limits and bottom alignment are window-local. Each
  preset offers Set default and a confirmable “Apply now…”, which replaces this window's frame and preserves
  open documents and terminals across every workspace in the window; other windows are unaffected. Reset frame
  reapplies the default preset. The app-local “Preview files before keeping them” switch, on by default,
  controls the preview slot: off, every file or spec open keeps its own tab and no open waits out the
  double-click window.
- Projects holds the app-local Show hidden files switch and the shared project list with per-row removal.
- Terminal chooses Metal texture (default) or Skia (fallback) as an app-local preference and reattaches
  attached terminal views in every window without ending their host shells.
- GitHub (“Local GitHub”) runs `gh auth status` with prompts disabled and a 10-second timeout and reports
  Connected with the account line, or Not connected with the reason (not installed, not signed in, no
  response), with Refresh. SharpRail stores no credentials.

- Plugins (`PluginsSettings.cs`) lists every roster row of the host this window is connected to: icon, label, the
  version of an external plugin, origin, status, description, a contribution summary, the reason of a failed or
  refused row, and the enabled plugins that depend on it. The switch disables at once (the host cascades
  dependents off) and enables after a “Also turn on …?” confirmation when disabled dependencies must come too,
  sent as one batch. Failed rows offer Retry; Rescan re-reads the plugin roots; the External plugin directories
  editor adds and removes roots as one `plugin-paths` change each. Rows are keyed by plugin id and replaced only
  when their roster entry changes. A remote host's plugins' UI halves run in this app.
- A plugin section shows the plugin's own control under its label; when the plugin leaves, an open section falls
  back to Plugins.

## Not yet ported

- Terminal replayed-output size and a Windows shell picker.
- A project picker in the Create workspace dialog; it shows only the current project.
- A notice dialog distinct from confirmation for failures with no recovery.
- Toast-based error reporting for rejected dialog actions.
- A dedicated disabled treatment on theme controls while one theme mutation is in flight.
