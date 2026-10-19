#!/bin/sh
# Runs a command in a GitHub Actions step and, when it fails, says why on the run page: an annotation with the first
# error lines and the step summary with the log's tail (scripts/ci-diagnose.sh). The output is also kept in
# $RUNNER_TEMP/sharprail-ci/ for the job's diagnostics artifact. Outside GitHub Actions it just runs the command.
# SHARPRAIL_CI_QUIET=1 keeps a failure silent (an attempt that will be retried).
# Usage: sh scripts/ci-run.sh "<step title>" <command> [arguments...]
set -u
title=$1; shift
if [ "${GITHUB_ACTIONS:-}" != true ]; then exec "$@"; fi
dir="${RUNNER_TEMP:-/tmp}/sharprail-ci"
mkdir -p "$dir"
log="$dir/$(printf '%s' "$title" | tr -c 'A-Za-z0-9' '-')-$$.log"
status="$log.status"
{ "$@" 2>&1; echo $? > "$status"; } | tee "$log"
code=$(cat "$status" 2>/dev/null || echo 1)
rm -f "$status"
if [ "$code" != 0 ]; then
  [ "${SHARPRAIL_CI_QUIET:-}" = 1 ] || sh "$(dirname "$0")/ci-diagnose.sh" "$title" "$log" "$code" || true
fi
exit "$code"
