# Architecture lessons

- For SharpRail, upstream is [JetBrains/thinkrail:main](https://github.com/JetBrains/thinkrail/tree/main).

- A host/client request for this maquette means a C# host as well as a C# client. Do not infer reuse of the reference's Bun host or Pi integration. Embedded calls are direct; gRPC is only the remote adapter.
- A performance-research maquette request authorizes implementation and optimization checks, not benchmark runs. Run measurements only when explicitly requested; when asked to show the app, leave the packaged GUI running.
- Project readiness must be established before projecting restored document tabs. Test a fresh window/profile restoration; switching back in an existing window can hide startup bugs behind cached content.
- Keyboard overflow needs a focusable result list and Enter activation from search. Test duplicate titles by resource path; correct filtering alone does not prove keyboard selection.
- Capture only the prototype's window ID. Screen-region captures can include private apps when the user switches windows; remove an accidental capture and do not reuse its content.
- A custom subclass of an Avalonia templated control must select its base control's style key when reusing that template. Parsed/rendered Markdown does not prove scrolling; verify wheel input changes the offset of a document larger than its viewport.
- Pane context actions must be reachable by right-clicking tabs, unused header space and empty pane content. Menu construction alone is insufficient; test right-button input, menu opening, and canonical layout changes.
- Fixed-height tab chrome needs a fixed label size independent of document text preferences. Leave room for italic preview glyph overhang inside the measured label box.
- Match the reference title bar's actual controls. Do not add project or layout icons merely because their actions are useful; keep those actions in the existing panel, shortcuts or context menus.
- A custom title bar must handle pointer gestures on its full painted surface, including blank space and margins. Attaching move/zoom only to an inner grid without a background misses those clicks. Preserve button input and verify native double-click zoom as well as headless routing.
- Reselecting the active tab must advance navigation without rebuilding the tab strip. Rebuilding between clicks can leave the second native mouse press hitting unarranged replacement controls. Headless clicks that pump layout between presses conceal this failure; include a native sequence and a test without an intermediate layout pump.
- Use the reference icon asset for branch indicators; a Unicode branch-like symbol changes shape with the font and does not match the UI. Keep the branch name as plain text beside the icon.
- Build fractional grid sizes with numeric GridLength values. Interpolated definition strings use the current culture; decimal commas become extra rows or columns. Verify actual split geometry under a decimal-comma culture, including nested horizontal and vertical splits.
- A request for C# and Avalonia does not mean every control must be constructed in C#. Use compiled XAML for static layouts, styles and templates; reserve procedural construction for genuinely dynamic UI. Do not claim C# construction is inherently faster than compiled XAML.
- Follow the reference component for small interactions: tab search is a compact anchored popover shown only on actual overflow, not a modal dialog. Specs shorten spaced em/en dashes to middle dots and reveal role tags on hover or focus.
- Embedded Markdown controls default to a bottom-edge baseline. Supply the label's measured text baseline and test its alignment with the paragraph at multiple font sizes.
- Headless interaction tests must run the dispatcher event loop and render ticks before hit testing. Resolve rebuilt controls again, move the pointer off old tooltips, and wait for the requested selection rather than merely an already-open tab.
