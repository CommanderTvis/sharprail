#!/bin/sh
# Turns a failed step's log into annotations and a step-summary section.
# Usage: sh scripts/ci-diagnose.sh "<step title>" <log file> [exit code]
set -u
title=$1; log=$2; code=${3:-1}
[ -f "$log" ] || exit 0
escape() { sed -e 's/%/%25/g' -e 's/\r//g'; }
# MSBuild: "path(line,col): error CODE: message" becomes a file annotation; other error lines a plain one.
errors=$(grep -E ': error [A-Za-z]*[0-9]+:|(^|[^A-Za-z])(error|ERROR|fatal|FAIL|FAILED)[: ]|NETSDK[0-9]+|MSB[0-9]{4}|Unhandled exception' "$log" \
  | grep -Ev '(^|[^0-9])0 Error\(s\)|Warning\(s\)|Time Elapsed|error: .*retry|^::' | awk '!seen[$0]++' | head -n 12)
printf '%s\n' "$errors" | while IFS= read -r line; do
  [ -n "$line" ] || continue
  file=$(printf '%s' "$line" | sed -nE 's/^ *(.+)\(([0-9]+),([0-9]+)\): error .*/\1/p')
  if [ -n "$file" ]; then
    row=$(printf '%s' "$line" | sed -nE 's/^ *.+\(([0-9]+),([0-9]+)\): error .*/\1/p')
    col=$(printf '%s' "$line" | sed -nE 's/^ *.+\(([0-9]+),([0-9]+)\): error .*/\2/p')
    msg=$(printf '%s' "$line" | sed -E 's/^ *.+\([0-9]+,[0-9]+\): error //' | escape)
    printf '::error file=%s,line=%s,col=%s,title=%s::%s\n' "$file" "$row" "$col" "$title" "$msg"
  else
    printf '::error title=%s::%s\n' "$title" "$(printf '%s' "$line" | escape)"
  fi
done
printf '::error title=%s failed::Step "%s" exited with code %s; see the run summary for the first errors and the log tail.\n' "$title" "$title" "$code"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    printf '### Step failed: %s (exit code %s)\n\n' "$title" "$code"
    if [ -n "$errors" ]; then
      printf 'First error lines:\n\n```\n%s\n```\n\n' "$errors"
    else
      printf 'No error line matched; the log tail below is the best lead.\n\n'
    fi
    printf '<details><summary>Last 40 lines of the log</summary>\n\n```\n'
    tail -n 40 "$log" | sed 's/```/'"'''"'/g'
    printf '```\n</details>\n\n'
  } >> "$GITHUB_STEP_SUMMARY"
fi
