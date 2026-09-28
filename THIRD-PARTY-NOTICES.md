# Asset provenance

The visual layout follows Thinkrail's original-workspace.png and current
workspace specification. The logo, custom diff icon and unchanged
scripts/window-probe.m came from prototypes/ThinkRailNative (Copyright 2026
JetBrains s.r.o., Apache 2.0; licenses/Thinkrail-Apache-2.0.txt).

PNG icons came from that prototype's @remixicon/react 4.9.0 assets.
The collapse/expand vertical and horizontal ellipsis icons use the same package's original vector paths.
See licenses/RemixIcon.txt for Remix Icon License 1.0.

Geist Regular, Medium, SemiBold and Bold came from the reference's bundled assets.
Geist Book uses the reference native prototype's weight-370 NativeText face;
its family metadata was renamed to register alongside the other Geist faces.
JetBrains Mono Regular came from the reference's bundled font assets.
Their SIL Open Font Licenses are in src/SharpRail.UI/Assets/Fonts and are
included in the published app's Resources directory.

Markdown parsing uses Markdig (BSD-2-Clause). Avalonia, protobuf-net.Grpc,
grpc-dotnet, and other NuGet dependencies retain their package licenses.

Side resize projection adapts the constraint and delta-distribution rules from
react-resizable-panels 2.1.9 (Copyright 2023 Brian Vaughn, MIT).
See licenses/React-Resizable-Panels.txt; no JavaScript runtime is bundled.
