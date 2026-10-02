---
id: module-plugin-discord
type: module-design
status: active
title: Discord — Rich Presence as a builtin plugin
parent: module-plugin-api
depends-on: [module-plugin-api, module-plugin-ui-kit]
references: [submodule-host-plugins, submodule-ui-plugins]
tags: [plugins, discord]
---

# Discord — Rich Presence as a builtin plugin

Upstream: packages/plugin-discord/SPEC.md @ eb382d75e (CommanderTvis fork)

## Responsibility

Speaks Discord's local IPC protocol to publish what project and file the user has open on their Discord
profile, and answers `plugin.discord.status` truthfully about what it did and why. In the fork it moved out
of the server's `discord` module (a fork of https://github.com/Azn9/JetBrains-Discord-Integration); SharpRail
never had it in core, so this plugin is its first home here.

It ships `EnabledByDefault = false`: Rich Presence is the one feature where "on by default" would publish
something to everyone who can see the user's Discord profile without them choosing to. Turning it on is done
the same way as any other plugin, from Settings › Plugins. There is no second, plugin-local on/off switch,
since a plugin's settings type may not declare `enabled` (contract intake refuses it) and the roster toggle
already gates activation, and with it the disposer that closes the IPC socket.

## Projects

| Project | Holds |
| --- | --- |
| `Contract/SharpRail.Plugins.Discord` | `DiscordPlugin.Manifest`, `DiscordContract` (the two methods and the status channel), the wire records, `DiscordSettings` and the snowflake rule (`DiscordValues`) |
| `Host/SharpRail.Plugins.Discord.Host` | `DiscordHost` (the `PluginHostModule`), `DiscordIpc` (the IPC client), `PresenceDecision` (the publish/redact decision and the status mapping), `DiscordRuntime` (the connection lifecycle) |
| `UI/SharpRail.Plugins.Discord.UI` | `DiscordUI` (the `PluginUIModule`: the settings section and presence reporting), `DiscordSettingsView` (compiled XAML) |

The host half references only the contract and `SharpRail.Plugins.Api.Host`; the UI half only the contract,
`SharpRail.Plugins.Api.UI`, the kit and Avalonia. `Host.Core/Plugins/BuiltinPlugins.cs` and
`UI/Plugins/BuiltinPlugins.cs` list it.

## What the fork moved, and how it maps

- `ipc.ts` becomes `DiscordIpc`: the handshake, length-prefixed frame read and write (ping answered with pong,
  close ends the connection, an `ERROR` event's message kept as `LastError`), and the Unix-socket discovery
  across the paths Discord's Electron client and its Flatpak and Snap packagings use (`discord-ipc-0` to `-9`
  under `XDG_RUNTIME_DIR`, `TMPDIR`, `TMP`, `TEMP`, the Darwin user temp directory and `/tmp`, each also under
  `app/com.discordapp.Discord` and `snap.discord`). `System.Net.Sockets` with a `UnixDomainSocketEndPoint`
  replaces Node's `net`.
- `presence.ts` becomes `PresenceDecision.For` and `PresenceDecision.Status`, unchanged: with no `enabled`
  setting the decision only ever answers `Silent` (no valid application id), `Clear` (a blocked project, or no
  project open) or `Publish`, and `DiscordConnectionState` has no `off` member.
- `host/lifecycle.ts`'s `createDiscordRuntime` becomes `DiscordRuntime`, built per activation by
  `DiscordHost.ActivateAsync`, so a disable and enable starts clean rather than inheriting a previous
  activation's retry floor. Its state is guarded by a lock, since method calls arrive on any thread.
  A handshake completing after disable is discarded; a stopped activation cannot reconnect or publish.
- The fork's `AppConfig.discord` becomes this plugin's settings record, `DiscordSettings` (`ApplicationId`,
  `BlockedProjectIds`, `ShareFileName`), with its defaults filled by the host's namespace validation.
- The methods are `plugin.discord.presence` (`DiscordPresenceParams` → `DiscordStatus`) and
  `plugin.discord.status` (`DiscordStatusParams` → `DiscordStatus`); the push is the declared state channel
  `status`, snapshot method `status`, keyed by nothing, so the settings section reads it as an ordinary
  subscription rather than polling.
- `web/reportPresence.ts` becomes `DiscordUI`'s `WatchHost` over exactly the projection it needs (the active
  workspace, the context project, the projects and the active editor), still debounced 500 ms with a
  `DispatcherTimer`, and reported once at activation.
- `web/DiscordSettings.tsx` becomes `DiscordSettingsView`, registered with `SettingsSection`. It reads
  `Settings<DiscordSettings>()`, writes through `UpdateSettingsAsync`, follows `OnSettings`, and lists projects
  from `Host().Projects` through `WatchHost`. The share-file-name button becomes a `ToggleSwitch` and each
  blocked-project row a `CheckBox` tagged with the project id, which is what the fork's `aria-pressed` buttons
  were.
- `web/DiscordMark.tsx` becomes `assets/discord.svg`, preserving the fork's path and currentColor fill.
  The settings section uses `asset:discord.svg`; the host stages and serves it to remote clients.
  The roster retains the fork's `discord` Remix glyph, mapped to the kit's bundled line icon.

## Get right

- Socket discovery cannot depend on the shell's environment. On macOS Discord's socket lives under the per-user
  temp directory, which a process normally learns from `$TMPDIR`, but an app launched from Finder has no login
  shell's environment, so the search asks the OS (`getconf DARWIN_USER_TEMP_DIR`, cached) after the variables
  and before the `/tmp` fallback. `SHARPRAIL_DISCORD_IPC_DIR` (the fork's `THINKRAIL_DISCORD_IPC_DIR`)
  overrides the whole search: the isolation seam for the checks, which must not find the developer's real
  Discord, and an escape hatch for an unusual setup.
- A withheld file name publishes no line at all, rather than a false one: `Details` is null and
  `SET_ACTIVITY` omits the key.
- The elapsed-time anchor resets on a project change, not a file change.
- Connection retry is floor-limited (5 s), not looped. `status` retries rather than reporting a cached
  failure, and a settings change clears the failure and the floor for an immediate retry.
- The UI half never writes settings locally: a change is sent and the section updates when the snapshot
  carrying it arrives. An application id that is not a snowflake stays in the field and is never sent; an
  empty one is sent, since it is the choice that silences the plugin.
- Optional members of every wire record have defaults, so a null the writer omits (`PluginJson` skips nulls)
  still reads back under strict deserialization.

## Limits inherited from the fork

- No Rich Presence artwork. `SET_ACTIVITY`'s `assets` field is omitted: it needs image keys uploaded to the
  registered Discord application ahead of time, which is per-user setup this plugin has no UI for.
- No background reconnect loop. Socket closure clears the connection; publishing or reading status retries
  subject to the five-second failure floor.
- No Windows named-pipe transport; SharpRail's hosts run on macOS and Linux.

## Checks

`tests/SharpRail.Checks/DiscordChecks.cs`, run by the full suite, `-- --plugins` and `-- --discord`:

- the fork's `presence.test.ts`: every decision (publish, blocked clear, no-project clear, silent by empty and by
  non-snowflake id, file-name redaction, a withheld name indistinguishable from no file, the anchor holding
  across a file change) and the status mapping;
- the fork's `lifecycle.test.ts`, against a fake Discord on a real Unix socket: a failed first attempt reports
  `Unavailable`, a socket appearing within the retry floor is not noticed, and a settings change clears the
  floor so the next status connects;
- the fork's `discord.spec.ts` against the app with its in-process and remote hosts and a fake Discord: off by default,
  enabled from Settings › Plugins, the default application id is a snowflake, the open project reaches
  Discord's `SET_ACTIVITY`, clearing the id silences it, an invalid id is kept in the field but never stored,
  a valid one survives reopening and is persisted, and a blocked project stays blocked.
- Additional transport checks cover disabled lifecycle, the SVG asset, presence/status call parity, status
  pushes and redaction over gRPC. Lifecycle coverage holds a handshake until after disable and checks that
  it cannot publish. IPC coverage checks fragmented frames, ping/pong, errors and socket-close notification.

Native appearance, published output and the full repository gates remain unverified.
