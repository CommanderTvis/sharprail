# Claude Code plugin payload

The hook scripts (`scripts/*.sh`, `scripts/*.bat`, `scripts/hook.ps1`), `hooks/hooks.json` and `tests/test-hooks.*`
are adapted from the ThinkRail fork's Claude Code plugin payload (`packages/plugin-claude-code`; Copyright 2026
JetBrains s.r.o., Apache License 2.0, see `licenses/Thinkrail-Apache-2.0.txt` at the repository root). tereditor's
endpoints, environment variables and tool names replace the originals, and much of the shell is unchanged from the
original, so these files stay under Apache-2.0. `scripts/edit-diagnostics.sh` and `scripts/lsp-permission.sh` are
tereditor's own. Everything else in this plugin is licensed as tereditor.
