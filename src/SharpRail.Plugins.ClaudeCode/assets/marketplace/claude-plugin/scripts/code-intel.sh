#!/bin/sh
# Code-intelligence reminders for Claude Code sessions SharpRail starts, including subagents (plugin hooks fire for
# their tool calls too, and SubagentStart reaches the subagent that never sees the appended system prompt).
#
#   code-intel.sh session    SessionStart: a short reminder naming lsp_navigate and lsp_diagnostics.
#   code-intel.sh subagent   SubagentStart: the same, for the subagent.
#   code-intel.sh pretool    PreToolUse on Grep, Glob and Bash: when the search targets a C#/code symbol, an
#                            advisory note with the equivalent lsp_navigate call. Never blocks: no permission
#                            decision, always exit 0. Rate-limited per session and agent (first time, then every
#                            CODE_INTEL_EVERY-th code-symbol search) with a counter under $TMPDIR.
#
# POSIX sh and awk only (no jq), so it runs wherever Claude Code runs hooks (sh on macOS/Linux, Git Bash on
# Windows). Silent without SharpRail's MCP server (THINKRAIL_MCP_URL), where the tools it names do not exist.

[ -n "${THINKRAIL_MCP_URL:-}" ] || exit 0

REMINDER='SharpRail code intelligence: for code questions use lsp_navigate before grep, by name with no position, e.g. {"operation":"references","query":"TypeName","workspace":"<absolute path>"} (also definition, implementation, hover; symbols/symbolBody with a path). grep misses implementations, overloads, partial classes, generated and platform-specific code; keep it for literals, config and docs. Run lsp_diagnostics with paths on every edited file before reporting. Pass the absolute workspace to every SharpRail tool.'

context() {
	# $1 event name, $2 text without double quotes or backslashes needing more than the escaping below.
	printf '{"hookSpecificOutput":{"hookEventName":"%s","additionalContext":"%s"}}\n' "$1" "$(printf '%s' "$2" | sed 's/\\/\\\\/g; s/"/\\"/g')"
}

case "${1:-}" in
	session) context SessionStart "$REMINDER"; exit 0 ;;
	subagent) context SubagentStart "$REMINDER Report how many lsp_navigate and lsp_diagnostics calls you made."; exit 0 ;;
	pretool) ;;
	*) exit 0 ;;
esac

# One awk pass reads the hook JSON and prints "<key>\t<symbol>\t<kind>\t<cwd>" for a code-symbol search, else nothing.
VERDICT=$(awk -v q="'" '
function field(name,    m, rest, out, c) {
	if (!match(input, "\"" name "\"[ \t\r\n]*:[ \t\r\n]*\"")) return ""
	rest = substr(input, RSTART + RLENGTH); out = ""
	while (length(rest) > 0) {
		c = substr(rest, 1, 1)
		if (c == "\\") {
			c = substr(rest, 2, 1)
			if (c == "u") { c = unicode(substr(rest, 3, 4)); rest = substr(rest, 7) } else { rest = substr(rest, 3) }
			if (c == "n" || c == "t" || c == "r") c = " "
			out = out c; continue
		}
		if (c == "\"") break
		out = out c; rest = substr(rest, 2)
	}
	return out
}
function unicode(hex) {
	hex = toupper(hex)
	return hex == "0022" ? "\"" : hex == "0027" ? q : hex == "0026" ? "&" : hex == "003C" ? "<" : hex == "003E" ? ">" : hex == "002B" ? "+" : hex == "005C" ? "\\" : "?"
}
function symbol(p,    q) {
	q = p
	gsub(/\\b|\\<|\\>|\^|\$/, "", q)
	sub(/^(public |private |internal |protected |static |sealed |abstract |partial |async |override )*(class|interface|struct|record|enum|void|new) +/, "", q)
	sub(/ *\\?\( *$/, "", q)
	if (q !~ /^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$/) return ""
	if (length(q) < 4 || q !~ /[A-Z]/ || q !~ /[a-z]/) return ""
	return q
}
function unquote(t) { gsub("^[\"" q "]+|[\"" q "]+$", "", t); return t }
function docs(path) { return path ~ /\.(md|txt|json|toml|ya?ml|xml|props|targets|csproj|slnx|sh|ps1|bat|lock|html|css|svg)$/ }
function code(path) { return path == "" || path ~ /\.cs$|\.cs[^a-z]|(^|[^a-z])cs$|^\.?\/?(src|tests)(\/|$)|\*\*/ || path !~ /\./ }
{ input = input $0 "\n" }
END {
	tool = field("tool_name"); cwd = field("cwd"); key = field("session_id") "-" field("agent_id")
	found = ""; kind = "references"
	if (tool == "Grep") {
		scope = field("path") " " field("glob") " " field("type")
		if (!docs(field("path")) && !docs(field("glob")) && (code(field("path")) || field("type") == "cs" || field("glob") ~ /\.cs/)) found = symbol(field("pattern"))
	} else if (tool == "Glob") {
		g = field("pattern"); n = split(g, parts, "/"); base = parts[n]
		if (base ~ /\.cs$/ || base !~ /\./) { gsub(/\*|\.cs$/, "", base); found = symbol(base); kind = "workspaceSymbols" }
	} else if (tool == "Bash") {
		command = field("command"); n = split(command, segments, /\||;|&&/)
		for (s = 1; s <= n && found == ""; s++) {
			raw = split(segments[s], pieces, /[ \t\n]+/); w = 0
			# Rejoin quoted arguments that contain spaces, such as "class Foo".
			for (k = 1; k <= raw; k++) {
				t = pieces[k]; open = substr(t, 1, 1)
				if ((open == "\"" || open == q) && (length(t) == 1 || substr(t, length(t)) != open))
					while (k < raw) { k++; t = t " " pieces[k]; if (substr(pieces[k], length(pieces[k])) == open) break }
				words[++w] = t
			}
			i = 1
			while (i <= w && words[i] == "") i++
			if (words[i] == "git" && words[i + 1] == "grep") i++
			if (words[i] !~ /^(rg|grep|egrep|sed)$/) continue
			sed = words[i] == "sed"; pattern = ""; paths = ""; codey = 0
			for (j = i + 1; j <= w; j++) {
				t = unquote(words[j])
				if (t ~ /^(-t|--type)$/ && words[j + 1] ~ /cs/) codey = 1
				if (t ~ /^(--type=|-t)cs$/ || t ~ /\.cs/) codey = 1
				if (t ~ /^-(g|t|T|A|B|C|m|f|-glob|-type|-max-count|-type-not)$/) { j++; continue }
				if (t ~ /^-e$/) { j++; t = unquote(words[j]); pattern = t; continue }
				if (t ~ /^-/) continue
				if (pattern == "") { pattern = t; continue }
				paths = paths " " t
				if (docs(t)) codey = codey ? codey : -1; else if (code(t)) codey = 1
			}
			if (sed) { if (match(pattern, /\/[A-Za-z_][A-Za-z0-9_.]*\//)) pattern = substr(pattern, RSTART + 1, RLENGTH - 2); else pattern = ""; if (paths !~ /\.cs/) codey = 0 }
			if (paths == "" && !sed && codey == 0) codey = 1
			if (codey == 1) found = symbol(pattern)
		}
	}
	if (found != "") printf "%s\t%s\t%s\t%s\n", key, found, kind, cwd
}' 2>/dev/null) || exit 0

[ -n "$VERDICT" ] || exit 0
TAB=$(printf '\t')
KEY=${VERDICT%%"$TAB"*}; REST=${VERDICT#*"$TAB"}
NAME=${REST%%"$TAB"*}; REST=${REST#*"$TAB"}
KIND=${REST%%"$TAB"*}; CWD=${REST#*"$TAB"}

# Rate limit: the first code-symbol search of each session/agent, then every CODE_INTEL_EVERY-th.
EVERY=${CODE_INTEL_EVERY:-5}
DIR=${CODE_INTEL_STATE:-${TMPDIR:-/tmp}/sharprail-code-intel}
KEY=$(printf '%s' "$KEY" | tr -c 'A-Za-z0-9_-' '_')
mkdir -p "$DIR" 2>/dev/null
COUNT=0
[ -f "$DIR/$KEY" ] && read -r COUNT <"$DIR/$KEY" 2>/dev/null
case "$COUNT" in '' | *[!0-9]*) COUNT=0 ;; esac
COUNT=$((COUNT + 1))
printf '%s\n' "$COUNT" >"$DIR/$KEY" 2>/dev/null
[ $(((COUNT - 1) % EVERY)) -eq 0 ] || exit 0

context PreToolUse "Advisory (SharpRail): \"$NAME\" looks like a code symbol. Text search misses implementations, overloads, partial classes and generated or platform-specific code; lsp_navigate answers this semantically: {\"operation\":\"$KIND\",\"query\":\"$NAME\",\"workspace\":\"$CWD\"}$( [ "$KIND" = references ] && printf ' (or definition, implementation)'). Keep grep for literals, config and docs."
exit 0