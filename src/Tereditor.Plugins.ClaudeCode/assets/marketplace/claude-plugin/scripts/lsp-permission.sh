#!/bin/sh
# PreToolUse on tereditor's language tools: approves the calls that only read, so they run without a permission prompt,
# as Claude Code's built-in LSP tool does. A call that writes files or changes servers or their settings gets no decision
# and keeps Claude Code's normal permission flow. Deny and ask rules still apply to an approved call.
#
#   lsp_navigate, lsp_diagnostics           approved (read-only; diagnostics may start the file's server)
#   lsp_status                              approved for status, servers, catalog, capabilities, configurationSchema,
#                                           configuration, logs and projects; not for retry, restart, stop or configure
#   lsp_code_actions                        approved for list and preview; not for apply
#   lsp_rename, lsp_format                  approved for preview; not for apply
#
# Fails closed: every "operation" key anywhere in the input must name an approved operation (none means the tool's
# read-only default), else no decision. POSIX sh and awk only. Silent without tereditor's MCP server (THINKRAIL_MCP_URL).

[ -n "${THINKRAIL_MCP_URL:-}" ] || exit 0

awk '
function values(name,    rest, out, value, keys, found) {
	# Every value of the key anywhere in the input, space separated, or "?" when one of them is not a plain word (empty,
	# escaped, a number, an object), which cannot be judged. A key cannot hide inside a JSON string: its quotes are escaped.
	keys = 0; rest = input
	while (match(rest, "\"" name "\"[ \t\r\n]*:")) { keys++; rest = substr(rest, RSTART + RLENGTH) }
	found = 0; out = ""; rest = input
	while (match(rest, "\"" name "\"[ \t\r\n]*:[ \t\r\n]*\"[A-Za-z_]+\"")) {
		value = substr(rest, RSTART, RLENGTH); sub(/"$/, "", value); sub(/.*"/, "", value)
		out = out " " value; found++; rest = substr(rest, RSTART + RLENGTH)
	}
	return found == keys ? out : "?"
}
function all(list, allowed,    n, items, i) {
	n = split(list, items, " ")
	for (i = 1; i <= n; i++) if (index(" " allowed " ", " " items[i] " ") == 0) return 0
	return 1
}
{ input = input $0 "\n" }
END {
	prefix = "mcp__plugin_tereditor_tereditor__"
	n = split(values("tool_name"), names, " ")
	if (n != 1 || index(names[1], prefix) != 1) exit
	tool = substr(names[1], length(prefix) + 1)
	operations = values("operation")
	if (tool == "lsp_navigate" || tool == "lsp_diagnostics") ok = 1
	else if (operations == "?") ok = 0
	else if (tool == "lsp_status") ok = all(operations, "status servers catalog capabilities configurationSchema configuration logs projects")
	else if (tool == "lsp_code_actions") ok = all(operations, "list preview")
	else if (tool == "lsp_rename" || tool == "lsp_format") ok = all(operations, "preview")
	if (ok) printf "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"allow\",\"permissionDecisionReason\":\"tereditor: read-only %s call\"}}\n", tool
}' 2>/dev/null
exit 0