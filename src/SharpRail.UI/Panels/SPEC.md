---
id: submodule-ui-settings
type: submodule-design
status: active
title: "Panels: Settings and shared dialogs"
parent: module-ui
---

# Panels: Settings and shared dialogs

Upstream: apps/web/src/panels/SPEC.md (revision: [STATUS.md](../../../STATUS.md#upstream-tracking))

## Responsibility

The modal surfaces the workbench opens: the Settings overlay (`SettingsView.axaml` / `.cs`, a control shown in the workbench's `OverlayHost`), the shared
dialog card (`DialogWindow.axaml` / `.cs`) with its factory `Dialogs`, the Start work dialog
(`NewWorkspaceDialog`), the workspace search dialog (`SearchDialog`), and the local GitHub CLI probe
(`GitHubStatus.cs`). Static layout, page templates and
styles live in compiled `.axaml`; C# fills content and wires host interaction. The workbench panels that open
these surfaces are specified in [../Panels.SPEC.md](../Panels.SPEC.md).

Compact numeric settings retain a complete outline and vertically centered content; their nested
Fluent template controls fit the compact height instead of clipping at the default input minimum.

## Boundary

The terminal renderer chooser is visible only on macOS. Windows and Linux terminals use
Skia, so Settings never offers an unavailable Metal renderer there, and a profile defaults to Skia off macOS.

Settings › Highlighting imports a TextMate JSON grammar from a file or pasted text,
with comma-separated filename patterns, and lists custom entries with Remove actions.
Adding an existing scope replaces it. Validation and file reads run off the dispatcher;
errors preserve the form and leave host settings unchanged. The grammar payloads and
patterns are shared through host state, with a 2 Mi character aggregate limit, and
apply to attached file/source/diff editors without restarting or changing documents.
“Want some highlighting?” offers “Ask Claude to add it” and “Ask Codex to add it”.
Each uses the registered agent launcher in the current workspace with an initial prompt
explicitly invoking the builtin `add-highlighting` skill. Buttons are disabled when
the corresponding launcher or workspace is unavailable; these actions do not start
agents merely by opening Settings. Static controls live in the compiled page template.

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
- Every dialog is content in the window overlay, like Settings. Over a workbench window a `DialogWindow` is not an owned
  window but a layer of the window's own (`WorkbenchWindow.Present`, one `OverlayHost` per dialog, stacked above Settings
  and each other), so clicking the owner cannot re-order or re-create it, which is what made the owned Clone repository
  dialog flicker on macOS. The scrim takes every press, so a click outside does nothing. Escape and Mod+W close, focus goes
  to the dialog's first control when its code focuses none, focus returns to the control that had it (also when one dialog
  follows another at once), and the window's commands stay out of reach while one is open. The layer clamps the card to
  the window (height first, then width): the heading and the actions stay in view and the fields scroll in between, so no
  dialog can outgrow the window. Callers keep `Opened`, `Closed`, `Close(result)` and `ShowDialog<T>`; on Android the card
  docks to the bottom edge, and in the browser the dialog is drawn in the page canvas like everything else. This covers
  Create project, Open by path, Clone repository, Notice, Confirm, Prompt, AskToSave, the save-conflict and
  discard-unsaved dialogs, the local-browser approval, New workspace, Search, Existing worktree, the path browser, About,
  the GitHub, Claude Code, Codex, Air Context enable, Blueprint, setting-value and chat dialogs.
- Native windows that stay: the Mermaid full-screen viewer (`DialogWindow.InWindow = false`: a resizable viewer sized from
  its window), the Language settings, logs and terms windows and the Air Context consent and logs windows (own `Window`
  classes, deliberately separate), and a dialog with no owner or an owner that cannot host one (About from the macOS menu
  with no window). Those use the owned-window path, which in the browser is still placed by `.sharprail-dialog`.
- Escape closes as a dismissal. `Dialogs.Confirm` focuses Cancel initially; `Dialogs.Prompt` and
  `Dialogs.HostPath` focus their first field; `Dialogs.AskToSave` focuses Save.
- `Dialogs.HostPath` is the Open project by path dialog: it says the path belongs to the computer
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

## Create project

The Add project menu exposes Create project before the existing open/path/clone actions. The compiled
`NewProjectFields` asks for an existing parent folder and a new folder name; local hosts offer the native
folder picker and remote hosts accept a path on the host. The caller performs creation off the dispatcher.
Failures stay in the dialog with fields preserved; success opens Project Home. Existing targets are
rejected without altering their contents. Dismissing before submission creates nothing.

## Search

A query field over a results list. Typing searches after 150ms of quiet and cancels a superseded query;
results are grouped under their file, one row per matching line with its number and text. An empty result
says “No matches”, a truncated one says how many it shows, and a failed search shows the host's error.
Clicking a row closes the dialog with that hit; Escape closes it with none. Deliberately absent: regular
expressions, a case toggle, a glob filter and replace.

## Start work

- The title is the constant “Start work”; the dialog never renames itself under the user. A two-option
  target segment sits directly under it: New worktree or Project folder for Git projects. The
  only mode-aware prose is one line below it: “A separate git worktree on its own new branch.” or “No
  isolation: work lands in your project folder's current checkout.” The submit reads Create or Start.
- Git projects open on the worktree side. Plain folders open in Project folder mode with New worktree
  hidden and an explanation that there is no Git repository to isolate. All Start work entry points,
  including the rail's plus and project menu, remain available. Submitting enters Default without
  creating a repository; file and terminal tabs are available there. Branch discovery is skipped for
  plain folders. The worktree name and base picker are hidden in folder mode.
  Project Home hides its Create workspace card until Git discovery confirms a repository;
  Work in project folder and plugin project actions remain available for plain folders.
- The dialog names its project. When the host lists several open projects that row is a picker over them,
  checked on the dialog's own. Picking another project loads its branch catalogue through a separate
  project session, so the window does not move and the base returns to that project's default; a project
  whose branches cannot be read is reported and leaves the dialog where it was. Submitting for another
  project opens its Project Home first, then creates or enters the workspace there.
- Worktree mode shows the project and a base-branch trigger reading “From {base}” on one row,
  wrapping on narrow screens. The starting command has a visible label naming the selected agent
  and its arguments, or Terminal command for a plain shell. The base-branch flyout is a searchable
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
  before creation and follows a prefetched catalogue while untouched. Prompt text never replaces this
  suggestion. An agent's first task title names an untouched generated workspace after launch through
  the host's `set_title`; creation does not wait for a separate AI request. Editing Name takes precedence.
  A manually entered name is stored as the new workspace's label and supplies its branch:
  lowercase ASCII letters and digits, other runs replaced by hyphens, trimmed to 60 characters,
  with `workspace` as the empty-slug fallback. Existing local branches (including branch directories)
  receive a numeric suffix starting at `-2`. Later label renames do not rename branches.
- Folder mode hides the base picker and the name, and says where the work lands with an “On {branch}” line
  read from the catalogue's checked-out branch (absent when HEAD is detached); submitting enters the
  project's Default workspace, creating nothing.
- A “Start in a terminal with” row offers Nothing, Terminal and each registered agent launcher (W9),
  using the launcher's icon factory for plugin assets. An unavailable launcher is disabled with its reason
  as the tooltip. The chosen launcher's command is typed into a new
  terminal of the workspace once it opens, with the optional starting prompt.
- A multiline prompt above the launcher row uses “What do you want to work on?” and a 160-pixel
  minimum height, matching the fork's prompt surface. It is disabled with Nothing selected and
  focused when an agent is selected. Switching agents or targets preserves the draft. Enter submits;
  Shift+Enter inserts a newline. Both targets pass trimmed, nonempty text through the launcher's
  initial-prompt option; blank text or Nothing omits it.
- The launcher row also offers Terminal: it opens a shell with an optional starting command. Agents show
  their generated starting command in an editable field; edits retain the prompt and images as launcher
  arguments. Switching launchers preserves each command draft.
- Pasting an image into the prompt adds a removable preview. Up to eight PNG attachments of 8 MB each
  remain in memory until submission. Cancelled or removed drafts write no files. Submission uploads
  attachments to the host clipboard directory before starting the agent; both local and remote agents
  receive paths on their own host. Switching launchers preserves attachments. Codex attaches them with
  `--image`; Claude Code receives instructions to read the image files.
- Selected target and launcher choices have an accent outline and subtle primary fill, distinct from hover.
- Worktree submit returns the choice, and the workbench creates the worktree with the host-suggested path and
  the name-derived branch (or the suggested branch for an untouched name), persists the base as the
  workspace's comparison target, and opens it. A failed create reports the error and keeps the rail consistent.

## Settings

Appearance includes a shared Accent colour choice: a row of named swatches (Green is the default) plus an optional custom `#rrggbb` field, keyboard reachable, with the selection checked. The app itself is the preview: a choice applies to open windows at once, and each theme adjusts the lightness so the accent stays legible (Themes SPEC, Accent colour). It includes a shared File type icons choice of Plain (default, single-colour), Colored, Material and None (generic glyphs). It controls the core bundled icon set independently of Settings › Plugins. Choosing None keeps the style, so returning to a style restores it; changing the choice redraws Files, tabs and Changes in place.

A modal two-pane card laid out as upstream ThinkRail's: a section rail under uppercase muted group headers, INTERFACE (Appearance, Highlighting, Editor, Layout, Keymap, Terminal) then SYSTEM (Projects, Workspaces, Code Intelligence, Notifications, Host, GitHub, Plugins, About, then each
active plugin's own sections in registration order), with the selected section tinted. No AGENT group: SharpRail has no such pages and a scrolling
content pane. Page section headings share one `settings-heading` style: small, muted and uppercase (the text is uppercased wherever the class is applied, plugin pages included), under a short muted intro line. A row with a single control or status (a switch, a status line) is a `settings-row` border: name and description on the left, the control on the right, one 1 px hairline below; Appearance, Terminal, Notifications, GitHub, Highlighting, Editor, Layout, Host (bind address, port, token, approved browsers) and the Code Intelligence limits use it; its choice lists sit in one row each. About keeps its own fields, shared with the About window. Escape and the close button dismiss. It first opens on Appearance and afterwards on the section it was last left on in that window, the way a
Preferences window returns where it was left.
Settings is not a window: it is content in the workbench window's `OverlayHost` (`OverlayHost.axaml` / `.axaml.cs`), a
small reusable top layer over the workbench. The host shows one surface at a time over a dimming scrim that takes every
pointer press (nothing behind is reachable), confines Tab to the surface, closes it on Escape, pulls back focus that lands
behind it, and returns focus to the control that had it. While Settings is open the workbench's commands (close tab and
the other window chords) stay out of reach except the Settings chord, which only focuses the open overlay (and
`ShowSettings(section)` switches section); interface zoom still applies, since Settings sits inside the zoomed layout.
An owned window was avoided because the macOS native backend re-orders owned windows when the owner is clicked, which made
Settings flicker. The card is 832 wide and 80% of the window's height (at least 440), clamped to the window and centred in
it (`SettingsView.FitSize`); the close button, Escape and Mod+W dismiss, and `Closed` fires once. A compact workbench keeps its
page: the overlay leaves the page bar uncovered, fills the rest with no scrim, and the system Back returns from a section to
the list and from the list closes Settings.
Shared-setting writes go to the host and the view converges on the broadcast; a rejection shows “The host
could not save this change: …” inline and re-renders from host state.

- Host separates “Connect to another host” from “Share this app as a host” in distinct cards. Connecting
  uses another computer’s projects and terminals; sharing exposes this running app to other clients.
  Listener fields, status and actions belong only to the sharing card. Client-only Android/browser
  views instead label their current-host details and Disconnect action “Connected host”.
  Host controls the app-owned embedded listener shared by all its windows. Bind address defaults
  to 127.0.0.1 and port to 54123 (0 requests an available port); the random session token is masked.
  Start/Stop runs off the dispatcher, disables fields while pending or listening, reports errors
  inline with drafts preserved, and updates all open Host pages. Endpoint/token copy actions are
  available while listening. Closing Settings retains serving; app exit stops it. Configuration
  stays in memory and never enables automatic serving. A remote client explains that serving must
  be started at the embedded host instead of offering these controls.

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
- Small-number fields (font sizes, tab width, group limits) are sized from the digits of their maximum plus the stepper and padding, centred, on the row's right edge.
- Editor is one section: Editor font size (this device; separate from the interface size; pinch changes it too), Tab width
  (1–8, default 4) and Line numbers (default on) rows, then the wrapping rows. Tab width and line numbers apply to code
  documents; font family, line spacing and show-whitespace are not offered because the editor cannot honor them. The
  wrapping rows are two compact `settings-row` rows, File and Markdown, each on
  one line: name with a muted detail, a 40–240 integer field with a `symbols` suffix, and a `Limit lines` switch,
  in columns that line up across the rows. The field commits on Enter or focus loss (no Save button); an invalid
  draft is marked and reverts on focus loss, Escape reverts it. Defaults are 120 and 78, bounded.
  Invalid drafts stay local with a range error; Escape restores the host value; Enter saves when valid. A
  broadcast updates an untouched field and the switch in place, but keeps an edited draft.
- Layout lists Balanced, Focus, Review and the named custom presets. Custom save, rename and delete are shared
  host state; the default preset and side group limits are window-local. Each
  preset offers Set default and a confirmable “Apply now…”, which replaces this window's frame and preserves
  open documents and terminals across every workspace in the window; other windows are unaffected. Reset frame
  reapplies the default preset. The app-local “Preview files before keeping them” switch, on by default,
  controls the preview slot: off, every file or spec open keeps its own tab and no open waits out the
  double-click window.
- Layout offers no tab-mode or bottom-zone settings: tabs always live in Projects. In a window without a top bar the left side is not customizable, so the group limit reads "Right side groups".
- Projects holds the shared project list with per-row removal.
- Terminal chooses Metal texture (default) or Skia (fallback) as an app-local preference and reattaches
  attached terminal views in every window without ending their host shells. Below it, the host's replay
  size (Off, 16 KB, 64 KB, 256 KB, 1 MB) is shared state: a click sends the change and the selection moves
  when the broadcast arrives.
- Appearance, on Linux only, adds "Window decorations": Automatic (recommended), Integrated header or System title bar over the host's
  shared `window-decorations`, following the broadcast. An active `SHARPRAIL_WINDOW_DECORATIONS` environment override is named under the
  choices, since it wins. Open windows switch at once; the copy tells a user whose window manager keeps the old frame to open a new window.
- Notifications is the fork's `NotificationsSettings`: one "Enable notifications" switch over the host's
  shared `notifications` setting (default on), following the broadcast rather than the click. Turning it
  on also asks the system for notification permission, while the user is present. The line below says the
  operating system delivers them, or that this platform has none; the fork's browser-permission states
  have no counterpart.
- GitHub (“Local GitHub”) runs `gh auth status` with prompts disabled and a 10-second timeout and reports
  Connected with the account line, or Not connected with the reason (not installed, not signed in, no
  response), with Refresh. SharpRail stores no credentials.

- Plugins (`PluginsSettings.cs`) lists every roster row of the host this window is connected to: icon, label, the
  version of an external plugin, origin, status, description, a contribution summary, the reason of a failed or
  refused row (an external plugin switched on is refused, since external plugins do not run), and the enabled plugins that depend on it.
  A failed or refused row's switch shows off and cannot be used, except a refused row still switched on in host
  state: its switch shows on, so the user can turn it off. The switch disables at once (the host cascades
  dependents off) and enables after a “Also turn on …?” confirmation when disabled dependencies must come too,
  sent as one batch. Failed rows offer Retry; Rescan re-reads the plugin roots; the External plugin directories
  editor adds and removes roots as one `plugin-paths` change each. The card list is virtualized: only cards near
  the viewport are realized, and a card (`PluginCard.axaml`) is recycled across plugins rather than rebuilt. Each
  plugin keeps one card model for the page's life; a roster change updates that model in place, so a broadcast
  never recreates a card or moves the scroll, and unrelated broadcasts (roster and roots unchanged) do nothing.
  `--plugins-settings` checks a 48-plugin roster. A remote host's plugins' UI halves run in this app.
- Keymap (`KeymapSettings`, `KeymapPage` in `SettingsView`): a preset ComboBox (IntelliJ first, then custom presets) with New (duplicates the selected preset under a typed name), Rename, Duplicate and Delete (the last three only for custom presets; deleting the active one falls back to IntelliJ). Choosing a preset makes it active for every window. Under it a search box filters the actions, listed in collapsible groups with counts (the whole header row toggles, with no triangle, and a group without an icon has no icon slot; built-in: General, Navigation, View, Tabs and windows, Language; then a Plugins section with one subheading per plugin, showing its icon and name), search filtering across every group and expanding the matches, a Conflicts indicator that counts the chords two actions share and jumps to the first, a per-group Reset for custom presets, a Plugin not available group for stored bindings of plugins that are off, each row showing the action name, its category (none for a plugin whose group is its own name; never "Built in" and never the plugin name twice) and its one or two chords in the platform's symbols; the row's Edit button or a double-click records a chord for the first chord, a second Edit for the second, each shows `Press the shortcut…` and the pressed chord live, Escape cancels, Backspace or Delete clears. A taken chord names the other action and offers Reassign or Cancel; a terminal-reserved chord is refused with the reason. Reset to preset restores a row from the preset it was duplicated from (custom presets). IntelliJ is never edited in place: the first edit creates `My keymap` (numbered when taken) without a dialog, selects it and records into the copy. Changes are host-state changes; the page and every window refresh from the broadcast. `--keymap` checks it.
- A plugin section shows the plugin's own control under its label; when the plugin leaves, an open section falls
  back to Plugins.

## Not yet ported

- The fork's Settings redesign (Settings redesign, #710). The rail is not grouped into Agent, Interface and
  System, and pages keep their own control shapes rather than shared row, group, segmented-control and notice
  primitives. Free-text values (Editor line widths) commit on Enter or blur with no Save button: a valid, changed value is saved
  once, an invalid draft is marked and never sent, and Escape reverts the draft before it dismisses the window. Appearance has no Theme mode
  (System, Light, Dark) and Contrast (Default, High) controls with the specific theme behind a collapsed
  disclosure, and the editor line width is not a group of Appearance. Layout does not mark the card whose
  geometry matches the live frame as applied (derived by comparison, never stored, with its Apply disabled),
  mark the default beyond its button label (no Fallback state when the stored default is gone, no tooltip saying Reset uses it and that it is per window), offer an Add new layout card that
  captures the live frame, keep the group limits auto-saved in a collapsed Advanced disclosure, or put Apply
  now and Reset to default behind one confirmation. Terminal replay size reads as Scrollback, a host value
  that is not a preset selects none, and Notifications has no permission notices.
- A Windows shell picker.
- Keeping the Create workspace dialog open to retry after a rejected creation; the dialog has closed by
  then and the failure is a toast.
- Toasts for the remaining rejections that follow a confirmation (closing a project, initialising a
  repository, closing busy terminals); they still use the window's error line.

Settings › Host, while listening, offers a QR button beside the copy buttons. It draws the connect link
(`State/HostLink.cs`: the address another device can reach, and the session token) with `QrCodeView` only
while asked for, and hides it again when listening stops, because the code is the credential. A listener
on every interface is named by the computer's first network address; one on loopback says it cannot be
reached from another device.


Settings retain their mounted page through unrelated host-state broadcasts, including terminal titles and agent activity. Changes to settings, shared presets or the project list still reconcile the affected controls; background activity must not repeatedly replace an open page or lose its input state.

The 2026-10-18 regression reproduced Appearance page replacement on a terminal-title broadcast before the guard, then passed for Appearance, Layout and Projects while preserving refresh on a real appearance change. Actual Chrome Settings/RPC journeys passed at 1840×1340/DPR1 and 1437×907 with 125% interface zoom/DPR2, including repeated broadcasts, stable dialog geometry/visibility, pointer dismissal and recovered terminal input. These passes do not establish Safari/Firefox or physical Retina acceptance.

## Code Intelligence notes

The Code Intelligence pane and the Settings › Code Intelligence page both state, in one muted line
(`LanguageNotes.Lifetime`), that servers start on demand within the limits and that starting one by hand is for
editor features, and that agents can start the servers they need and manage the configuration. A headless check (`--lsp-ui`,
`--lsp-settings`) asserts the text in both places. Rows of running servers show who started them and the
agent call count with the time of the latest call.

The Code Intelligence tool window ends with a collapsible Running section (`LanguageRunningView`): every running
server on the host, grouped by workspace folder (full path in the tooltip) with the current workspace first, each
with its server, state, memory, idle time and a Stop that releases it (it restarts on its next request). The
header is the one-line summary ("20 running · 28 MiB"); the section starts collapsed when more than five run.
It polls the host's `running` operation every 5 s off the UI thread while shown.

Settings › Code Intelligence holds only configuration in a Limits section (`LanguageLimitsView`): the idle
timeout, memory budget (automatic or fixed, with the automatic derivation and memory pressure noted), the
explanation of when servers stop, and the C#, JVM and other count caps, saved as the `language-limits` host
setting and re-shown from its broadcast; its rows form two groups (timeout and budget, then the three caps) with one hairline between them and none between rows of a group or above Save. Settings shows no live server state. `--lsp-limits` checks it headless.

Settings › Terminal ends its host settings with a Memory guard section: a switch, the per-terminal and the total limit
(automatic or a fixed size) and a comma-separated list of protected programs, saved as one `terminal-memory` host setting and
re-shown from its broadcast. The section explains the escalation and says that the guard samples. The behaviour is in the
Terminals spec ("Memory guard").

Server rows are a grid at every width (static, in `LanguagePanel.axaml`): one header line holds the
status mark, the language icon, the name, settings and Start/Retry, with the status, icon and action
columns fixed-width (20, 28, 28 and 56 px) so they line up across rows; shared-size scopes are not used, because Avalonia's scope retains the column definitions, and through their bindings every row ever shown. Rows are dense: 4 px vertical padding, 2 px between detail lines, a 1 px hairline between rows. Every detail line (needs, install text, links, suggested-by, provenance, version) starts at the name's left edge: link buttons in rows use `0,2` padding so their text aligns, and Hide/Unhide share the suggested-by line, right-aligned. A plain suggested row stays within 56 px (`--lsp-narrow`). The pane has no Refresh button: it re-reads the inventory when mounted, on host broadcasts about tool paths, hidden servers and configuration, and every 3 s while visible, with one request in flight at a time (`Idle`) and stale replies for another workspace rejected. The workspace line is the Kit `PathLine` (folder icon, code font, middle-trimmed, full path as tooltip), the same control the Claude Code pane's scope uses. `RefreshRows` reconciles the list by server id, replacing only rows whose state changed, and `--lsp-narrow` asserts that 50 refreshes of 20 servers release replaced rows. The name fills the remaining width on one
line, trims with an ellipsis and shows itself as a tooltip; suggestion, status and exit details sit
below it, aligned to the name column, and only long free text wraps. The shared pane header trims its
title before its buttons, so no pane title runs under + or −. `--lsp-narrow` (also part of
`--lsp-ui`) asserts at 160, 220, 320 and 480px that status, icon, name and actions share one line,
action columns align across rows, the name stays on one line, detail text is no narrower than its
longest word, row actions stay visible inside the row, and pane titles end before their buttons.

A row whose server declares host tools lists them when expanded, and always while a required one is missing: the row
then reads `Needs <name>` once, in red, before any start: the tool list leaves out that line in the pane
(Settings, which has no row status, keeps it), and both build it with one helper. Each tool the next launch needs shows where it was found, that it is downloaded on first use (Bun, no error), or its install hint (selectable, with
Copy install hint), and offers Use path… (a file picker; the chosen path becomes the host-wide `language-tool:` setting,
changed off the UI thread) and Clear path when a path is set. Rows re-read the servers when the setting's broadcast
arrives, so Retry works without a restart. The rows are one shared control (`LanguageDependencyList`), which inherits
the host state from its pane and reports status through a bubbling event.

Rows can be hidden per project. Suggested, idle or unavailable rows show an inline Hide link; every row's context menu
offers Hide, or Stop and hide for a running server (it is stopped first, since a hidden server is not shown, suggested
or started for agents). Hidden rows leave the list and the Add dropdown, and a footer link reads `N hidden · Show`;
Show lists them again with Unhide, and the link then reads `N hidden · Collapse`. Hiding is a personal preference, so it
is host state (`ProjectLanguageHidden`, project root to server ids, changed through the `setProjectHidden` language
operation off the UI thread), never the shared `language.toml`; panes follow its broadcast. `--lsp-narrow` (also run by
`--lsp-ui`) asserts Hide, the footer, persistence across a reload, that a hidden server is not offered, Show and
Unhide, and that no row reads `Needs` twice.

Settings › Code Intelligence shows the selected server's tools in a System dependencies section under its details, with
the same control and actions; servers without declared tools show no section, and a server whose required tool is
missing carries a small red marker in the left list. The page re-reads the catalog (which carries the probe) when
`LanguageTools` or `LanguageServers` changes, so a path set in either place, or by another client, updates both.
`--lsp-dependencies` asserts the pane and the page.

Every host call of the pane is bounded (20 s to read or stop, 120 s to start): a call that never answers or throws leaves the status line with the reason and a Retry button, and the request slot is released, so a later refresh is never swallowed by a dead one. A workspace switch makes the retained panes inert while it runs and the Code Intelligence pane is made clickable and scrollable again when the switch settles or fails; it was previously restored by nobody, which left the pane frozen after any switch. `--lsp-narrow` asserts the timeout, the throwing call, the bounded log and the lag watchdog.

The Code Intelligence lifecycle log is `~/.sharprail/logs/code-intelligence.log` (about 2 MB, then moved to `.1`, one copy kept), written by a background thread so no caller waits for the disk; it holds names, ids and durations, never file contents. One line per event, `UTC-timestamp LEVEL tid=<thread> evt=<name> key=value ...`; operations log `<name>.begin` and `<name>.end` sharing an `id=` with `outcome=` and `durationMs=` (`pane.refresh` and `pane.rpc` in the pane, `host.language`, `host.server-start`, `host.initialize`, `host.project-graph`, `host.server-stop`, `host.server-exit`, `host.slot-wait`, `host.inventory-scan` in the host), `pane.state` names who changed the pane's enabled or hit-test state, `pane.layout` its size, viewport, extent and the ancestor that blocks input (`blockedBy`), `still-waiting` repeats every 5 s for any operation pending longer, and `ui-lag` is a WARN when the UI thread ran a no-op more than 250 ms late. To read a freeze: grep `ui-lag` (UI thread stalled), then `still-waiting` and `.begin` lines without a matching `.end` for the same `id` (a call that never completed), then the last `pane.layout` `hitTest`, `enabled` and `blockedBy` fields.

The pane re-reads the host every 3 s while a server is installing or initializing, after an action of its own, or until the first inventory arrives, and every 30 s otherwise (host broadcasts cover settings changes). The host reuses its file census and tool probes for 30 s. A row is replaced only when its content changed (presentation and dependency lists compare by content, since every reply builds new ones), so a poll never rebuilds unchanged rows under a click or drag; `--lsp-narrow` asserts it.
