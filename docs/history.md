# History

tereditor started on 2026-09-28 as SharpRail, a native C# and Avalonia port of ThinkRail, the web and desktop
workbench by JetBrains. It ported both JetBrains ThinkRail and a personal fork of it that added a plugin API,
builtin plugins (Claude Code, Codex, Blueprint, branch graph and others) and general improvements. The module specs
began as translations of ThinkRail's specs, and the first end-to-end checks translated its E2E scenarios.

Until 2026-11-02 the port followed both lines and recorded each sync; the last tracked JetBrains commit was
`d3aae2d04` (2026-10-21). On 2026-11-02 tracking ended: nothing is synced or ported from ThinkRail any more, and the
personal fork is no longer maintained. tereditor is now the only maintained line, and its specs describe tereditor
on their own terms. SharpRail was later renamed tereditor.

Some code and text still carry the old name, for example the `dev.thinkrail.tereditor` package id and the
`THINKRAIL_MCP_URL` variable. The WinGet source, once named `thinkrail`, is now `commandertvis`, matching the
`commandertvis/homebrew` tap. Files adapted from ThinkRail stay under Apache-2.0; see [NOTICE](../NOTICE) and [third-party notices](../THIRD-PARTY-NOTICES.md).
