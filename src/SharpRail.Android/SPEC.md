# Android — the workbench as a client of a remote host

No upstream counterpart: the reference has no Android client.

## Responsibility

The Android application: the workbench of `SharpRail.UI` compiled for Android and composed as a client of a
host that runs elsewhere. It owns the application and activity entry points, the windowing layer Avalonia's
Android backend lacks, the connect screen, the remembered host endpoint and the packaging of the native
terminal, editor and PDF libraries. It adds no workbench behaviour of its own; the one screen it contributes
is the connect screen, and the one page it replaces is Settings › Host.

## Boundary

- Owns: `MainApplication` (starts Avalonia and installs the windowing platform before any activity exists),
  `MainActivity` (the single activity), `ClientSession` (connect screen or workbench, never both),
  `ConnectWindow`, `HostEndpoint` (`host.json`), the Android half of `SettingsWindow` (`HostSettings.cs`),
  `Windowing/` and the project's native-library build hook.
- Forbidden: an embedded host, a local PTY, a terminal relay child or a served endpoint; a second copy of
  workbench sources; behaviour that only Android has outside this folder, other than a platform test in the
  shared code that owns it (`TerminalBackends`, `ScintillaEditor.IsSupported`, the Ghostty Skia control's
  touch and soft-keyboard input).

## Composition

`SharpRail.Android.csproj` targets `net10.0-android`, package id `dev.thinkrail.sharprail`, minimum Android 7.0
(API 24), ABIs `android-arm64` and `android-x64`. It is not in `SharpRail.slnx`, so desktop builds and CI need
no Android workload.

It compiles the sources, XAML and assets of `src/SharpRail.UI` by link, under the same assembly name and root
namespace (`SharpRail.UI`), instead of referencing that project: `SharpRail.UI.csproj` references
`SharpRail.Host.Remote` and the ASP.NET Core shared framework for its host-serving half, and neither exists
for Android. Three files are excluded: `Program.cs`, `Terminal/LoopbackTerminals.cs` and
`Panels/HostSettings.cs`. The remaining host-serving members are compiled out with `#if !ANDROID`: the desktop
composition in `App.OnFrameworkInitializationCompleted`, `Workbench.Listener` and the two listener
subscriptions in `SettingsWindow`. `HostSettings.cs` in this folder supplies the `ServingSettings` partial the
excluded file otherwise provides.

Consequences the shared sources must keep honouring:

- Every file under `src/SharpRail.UI` is an Android source unless the project excludes it. Code that needs
  `SharpRail.Host.Remote`, ASP.NET Core or a desktop lifetime goes under `#if !ANDROID` or into an excluded
  file; anything else must compile for both targets.
- The project still references `SharpRail.Host.Core`, `SharpRail.Host.Client`, the plugin API's UI entry, the
  kit, Scintilla, Ghostty.Avalonia and every builtin plugin's UI half, as the desktop app does. Core, the
  relay and the Metal terminal paths are therefore in the package, and never reached. `CA1416` is suppressed
  for that reason.
- Android's implicit usings `Android.App`, `Android.Widget` and `Android.OS.Bundle` are removed because they
  collide with Avalonia's control names throughout the shared sources.
- gRPC needs HTTP/2, so the managed HTTP handler is used (`UseNativeHttpHandler=false`).
- The package is untrimmed and runs on the JIT. `Directory.Build.props` builds the solution untrimmed, and
  Android compiles ahead of time only what it trims, so `RunAOTCompilation` is off. Marshal methods are
  disabled; Java peers register at run time.

`MainApplication.OnCreate` configures `App` with `UseAndroid()`, installs the windowing platform after
platform services are set up and calls `SetupWithoutStarting()`. `MainActivity` attaches itself to the
windowing platform, routes the system Back gesture to it and starts a `ClientSession` on the app's private
files directory. The activity is single-task, resizes for the soft keyboard and handles every configuration
change itself, so a rotation or a keyboard resizes the workbench instead of recreating it. Destroying the
activity disposes the session and closes every window.

## Windowing

Avalonia's Android backend has a single embedded view and no `Window`. The workbench, its dialogs and Settings
are `Window`s on every platform, so `Windowing/` registers an `IWindowingPlatform` (`AndroidWindows`) whose
windows (`AndroidWindow`, an `IWindowImpl`) each own an `AvaloniaView` and take over its surface, input and
text input.

Contract:

- A window needs a running activity; creating one without it throws. Windows cannot outlive the activity.
- The window shown without an owner while no other window fills the activity becomes the root. Its view
  replaces the activity's content inside a frame padded by the status bar, navigation bar, display cutout and
  soft keyboard, and the frame is painted in the window's background colour so the bars sit over it.
  Avalonia's automatic safe-area padding is turned off for its content, since the frame already applies it.
- Any other window is a transparent, undimmed Android dialog with its own view. Shown modally
  (`ShowDialog`) it takes the client size the window asks for, clamped to the area the system bars and
  keyboard leave visible, and is centred in that area; shown non-modally it fills that area. Dialogs are
  placed again whenever the insets change, so a dialog with a focused text field stays above the keyboard.
- The screen decides sizes: a window's minimum width and height are cleared when it is shown. Until its
  surface exists a window reports the size it will get (the screen for a filling window, the requested size
  for a modal one), so its first layout is not against an empty surface.
- A window renders only while it is shown and Android has given its view a surface.
- Popups are not native windows (`CreatePopup` returns null); Avalonia draws menus and flyouts in the owning
  window's overlay layer.
- Z-order is show order. Showing a window activates it and deactivates the others.
- System Back raises `TopLevel.BackRequested` on the window. Unhandled, a dialog is asked to close and may
  cancel; unhandled on the root, the activity moves the task to the background.
- Storage provider, launcher and clipboard requests from a dialog are answered by the root window, whose
  view was created in the activity.
- Hiding or closing detaches the Android view on a later dispatcher turn, because a window usually closes
  inside a touch event its own view is still dispatching.
- Title, icon, position, window state, move and resize drags, min/max size, topmost and decorations are
  accepted and ignored.

Invariants:

- Only the root's view is created in the bare activity. A view created there installs itself as the
  activity's insets listener, which a dialog's view must not take from the window beneath it; dialog views
  are created in a themed wrapper of the activity.
- The embedded root the Avalonia view creates for itself is closed and removed, so the window is the
  surface's only top level.
- One window fills the activity at a time. `ClientSession` closes the connect screen before it creates the
  workbench and the reverse on disconnect.

The layer depends on Avalonia internals and is pinned to Avalonia 12.1.3. Any Avalonia version change must
recheck it:

- Krafs.Publicizer exposes `TopLevelImpl`, its `InternalView`, `InvalidationAwareSurfaceView`, and
  `AvaloniaView.TopLevelImpl`, `_root` and `Dispose` from `Avalonia.Android`.
- `TopLevel.StartRendering`, `StopRendering` and `EnsureClosed` are non-public and bound by reflection.
- The project compiles against Avalonia's implementation assemblies rather than its reference assemblies
  (`UseAvaloniaImplementationAssemblies`), because the reference assemblies make the platform interfaces
  unimplementable.

## Connecting

The host is any SharpRail gRPC endpoint: a desktop app listening from Settings › Host, or
`SharpRail.Host.Remote`. The client speaks the same protocol and bearer token as a desktop remote client.

- `HostEndpoint` holds the address as typed and the session token. An address without a scheme is read as
  `http://`; one without a port uses 54123. Only `http` and `https` parse; whitespace or an empty host is
  rejected before any network call.
- `ConnectWindow` asks for both values. Connect probes the host's handshake through a `RemoteStateAdapter`
  off the UI thread with a ten-second limit; while it runs the fields are disabled and the button reads
  Cancel. A rejected token and an unreachable host are reported separately in the window, and the fields stay
  editable for a retry.
- On success the endpoint is saved, the connect window closes and `ClientSession` composes one `Workbench`
  with `remote: true`: remote state, terminal, plugin and per-window project adapters over one
  `HostConnection`, and a terminal factory fixed to the Skia renderer. Only the profile's first window entry
  is opened; later entries are dropped.
- The endpoint is stored as `files/.sharprail/host.json` in the app's private storage, beside the
  `profile.json` `ProfileStore` keeps in the same directory. On launch a remembered address with a token
  connects without a tap; the connect screen shows meanwhile and keeps any failure.
- Settings › Host shows the connected host's authority and a Disconnect button. Disconnect rewrites
  `host.json` with an empty token, closes the workbench's windows, disposes the adapters and shows the
  connect screen with the address still filled in.

Projects, settings, plugins' host halves and shells live on the host. The device holds only view state (the
profile) and the endpoint.

## Native libraries

| Library | Source | Built by |
| --- | --- | --- |
| `libGhosttyAvaloniaVt.so` | libghostty-vt at the commit pinned in `src/Ghostty.Avalonia/build-native.sh`, behind the Skia control's C shim | `src/Ghostty.Avalonia/build-android.sh` |
| `libSharpRail.Scintilla.so` | The patched Scintilla and SheenBidi sources of the desktop build, with libc++ linked statically | `src/SharpRail.Scintilla/build-android.sh` |
| `libpdfium.so` | `bblanchon.PDFium.Android`, pinned with the desktop PDFium packages | NuGet |

The `BuildNativeLibraries` target runs both scripts for `arm64-v8a` and `x86_64` before every build and adds
their outputs as `AndroidNativeLibrary` items. Both use the NDK r27d that `scripts/android-ndk.sh` installs
under `.tools/android-ndk`, target API 24 and align segments to 16 KB pages. The Ghostty library lands in the
shared cache `.tools/ghostty-avalonia/android/<abi>`, the Scintilla library in
`src/SharpRail.Scintilla/obj/native/android/<abi>`. PDF Preview's desktop PDFium packages are referenced with
their assets excluded.

Terminals draw with `GhosttySkiaView` only: `TerminalBackends` returns the Skia terminal on Android whatever
the renderer preference says. Text files open in Scintilla wherever `ScintillaEditor.IsSupported` holds, which
includes Android. Mermaid rendering stays macOS-only; Android shows the diagram source.

## Build

`scripts/android.sh [build|run|apk]` is the entry point (see [scripts/SPEC.md](../../scripts/SPEC.md)). It
needs the Android SDK in `ANDROID_HOME` and a JDK 17 or 21 in `JAVA_HOME`, and installs the `android` workload
into `.tools/dotnet` when missing. `build` and `run` use Debug unless `SHARPRAIL_CONFIGURATION` says
otherwise; `run` also installs on the connected device or emulator and launches the app. `apk` builds Release
and copies the signed package to `artifacts/android/SharpRail.apk`. No release keystore is configured, so the
package carries the SDK's default signature.

## Limits

- Cleartext HTTP is allowed (`usesCleartextTraffic`), as desktop remote connections use; the network must
  supply encryption. `https` addresses parse but have not been exercised.
- The session token is stored unencrypted in app-private storage. Backup is disabled in the manifest.
- One workbench window. New window is not adapted to Android.
- No automated on-device tests; the headless checks cover the shared touch and soft-keyboard input only.
- The Release package is about 84 MB, untrimmed, with unreachable desktop code inside.
- `licenses/` and the notices are not packaged into the APK.

## Validation

Verified on 2026-10-10 on an Android 16 arm64 tablet emulator against a `SharpRail.Host.Remote` on the
development Mac, in Debug and Release: the connect screen with the soft keyboard, automatic connection on
relaunch, the workbench, popup menus, modal dialogs including text entry above the keyboard, opening a project
by host path, the file tree with touch scrolling and file icons, Markdown preview, the Scintilla editor, a
live terminal typed into from the soft keyboard, Settings, Disconnect, and system Back closing dialogs. Cold
start of the Release package was about 2.3 s there. On a phone-sized emulator (1080x2340) the Release package
was checked for the connect screen, connecting, and the first frame: a fresh profile on a screen narrower
than 600 dp starts on the centre-only `focus` preset, with the panels behind their toggles.

Not verified: physical devices, working in the workbench on a phone beyond its first frame, input methods other than Gboard, TalkBack and other
accessibility services, plugin UI beyond what the default workbench shows, HTTPS endpoints and CI.
