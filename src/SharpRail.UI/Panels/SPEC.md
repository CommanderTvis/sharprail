# Panels: Settings and shared dialogs

Upstream: apps/web/src/panels/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))

## Responsibility

The modal surfaces the workbench opens: the Settings window (`SettingsWindow.axaml` / `.cs`), the shared
dialog card (`DialogWindow.axaml` / `.cs`) with its factory `Dialogs`, the Create workspace dialog
(`NewWorkspaceDialog`), and the local GitHub CLI probe (`GitHubStatus.cs`). Static layout, page templates and
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
- `Dialogs.HostPath` is the Open project from host path dialog: it says the path belongs to the computer
  running SharpRail, shows the native picker's failure when there was one, and returns the trimmed path.
- `Dialogs.Notice` is the single-button surface for a failure with no recovery inside it: a 384px card
  with an alert glyph beside the heading, the reason, and one focused default action (OK). Opening a project
  the user asked for by picker, path or recent entry that the host cannot open says “Couldn't open project”
  there with the host's reason, and leaves the shown project in place. Confirmation stays `Dialogs.Confirm`.
- A rejected action whose dialog has already closed is an error toast titled with what failed, never the
  window's error line: “Couldn't create workspace” (the branch catalogue read or the creation, with Git's
  own error) and “Couldn't remove the worktree”.
- `PrDialogs` holds the pull request compose and setup dialogs; their behaviour is specified with the
  Review panel in [Panels.SPEC.md](../Panels.SPEC.md).

## Create workspace

- A two-option target segment chooses where the work runs, both options always visible: New worktree or
  Project folder. The header is mode-aware so it names the operation truthfully: worktree mode is
  “Create workspace” / “A separate checkout on its own new branch. Files, changes, and terminals stay scoped
  to it.”; folder mode is “Work in project folder” / “Work directly in your project folder, with no
  isolation: changes land in your current checkout.” The submit reads Create or Start accordingly.
- The dialog always opens on the worktree side; there is no opener-chosen target.
- The dialog names its project. When the host lists several open projects that row is a picker over them,
  checked on the dialog's own. Picking another project loads its branch catalogue through a separate
  project session, so the window does not move and the base returns to that project's default; a project
  whose branches cannot be read is reported and leaves the dialog where it was. Submitting for another
  project opens its Project Home first, then creates or enters the workspace there.
- Worktree mode shows the project and a base-branch trigger reading “From {base}”. Its flyout is a searchable
  list grouped Local, then Remote with one subgroup per remote whose rows show the branch name without the
  remote; the full ref stays each row's identity and automation name. The host's default base is marked
  `default`. Enter in the search picks the first match, Escape returns to the trigger.
- The dialog opens from a cached branch catalogue and a fresh catalogue is prefetched in the background;
  when it lands it replaces the list and, unless the user already picked, the default base. The default base
  is whatever the host reports, never a literal `HEAD` sentinel that would be believed and persisted.
- Folder mode hides the base picker; submitting enters the project's Default workspace, creating nothing.
- Worktree submit returns the choice, and the workbench creates the worktree with the host-suggested path and
  branch, persists the base as the workspace's comparison target, and opens it. A failed create reports the
  error and keeps the rail consistent.

## Settings

A modal two-pane window: a section rail (Appearance, Line width, Layout, Projects, GitHub) and a scrolling
content pane. Escape and the close button dismiss. It opens on Appearance, sized to 80% of the owner's height.
Shared-setting writes go to the host and the view converges on the broadcast; a rejection shows “The host
could not save this change: …” inline and re-renders from host state.

- Appearance explains that mode and pair are shared by every window and client while each device reads its
  own light or dark setting, then offers two top-level cards, Fixed (“Use one theme everywhere.”) and Match
  system. Fixed shows the theme list. Match system shows Light theme and Dark theme selectors filtered to
  that appearance plus a “Current on this device” row reading `<Light|Dark> → <palette> <theme>`; either slot
  may be normal or high contrast. First enable sends mode and the derived pair atomically; slot edits send the
  complete pair; returning to Fixed changes only the mode, keeping both choices. An unavailable or
  wrong-appearance configured theme is disclosed beside the fallback and never written back. Exactly one
  theme mutation may be in flight from this page: the mode cards, the theme list and both slot selectors
  take their real disabled state, painted from the disabled roles, until the request settles, so rapid
  complete-pair writes cannot overwrite one another with stale sibling slots. The page
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
  reapplies the default preset.
- Projects holds the app-local Show hidden files switch and the shared project list with per-row removal.
- Terminal chooses Metal texture (default) or Skia (fallback) as an app-local preference and reattaches
  attached terminal views in every window without ending their host shells. Below it, the host's replay
  size (Off, 16 KB, 64 KB, 256 KB, 1 MB) is shared state: a click sends the change and the selection moves
  when the broadcast arrives.
- GitHub (“Local GitHub”) runs `gh auth status` with prompts disabled and a 10-second timeout and reports
  Connected with the account line, or Not connected with the reason (not installed, not signed in, no
  response), with Refresh. SharpRail stores no credentials.

## Not yet ported

- A Windows shell picker.
- Keeping the Create workspace dialog open to retry after a rejected creation; the dialog has closed by
  then and the failure is a toast.
- Toasts for the remaining rejections that follow a confirmation (closing a project, initialising a
  repository, closing busy terminals); they still use the window's error line.
