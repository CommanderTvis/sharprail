#!/bin/sh
# PostToolUse on Edit, Write and NotebookEdit: relays the hook input to the tereditor host that owns this terminal and
# prints its answer, the edited files' diagnostics from the language servers already running (errors, then warnings,
# capped), as the hook's additionalContext. The host starts no server; it formats the whole hook output, so this script
# parses nothing and needs only curl.
#
# Silent outside tereditor (no THINKRAIL_MCP_URL), without curl, when the host is gone or refuses the terminal's token, and
# when the host has nothing to say (204). A host that does not answer in time gets one short note. Always exits 0.

[ -n "${THINKRAIL_MCP_URL:-}" ] || exit 0
command -v curl >/dev/null 2>&1 || exit 0

LIMIT=${EDIT_DIAGNOSTICS_TIMEOUT:-9}
case $LIMIT in '' | *[!0-9]*) LIMIT=9 ;; esac
BODY=$(curl --silent --fail --max-time "$LIMIT" --header "Content-Type: application/json" --data-binary @- \
	"$THINKRAIL_MCP_URL/edit-diagnostics" 2>/dev/null)
case $? in
	0) [ -z "$BODY" ] || printf '%s\n' "$BODY" ;;
	28) printf '{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"tereditor: diagnostics for this edit did not arrive within %s s; run lsp_diagnostics with its path."}}\n' "$LIMIT" ;;
esac
exit 0