#!/bin/sh
# Points .tools/<name> at the shared, versioned install of each tool: link-tools.sh <mise> <name>=<tool@version>...
# The store keeps one copy per pin (see mise.toml), so checkouts at the same pin share it and a version bump gets
# a new directory. Tools other than the .NET SDK are made read-only once installed: the SDK stays writable because
# `dotnet workload install` (Android, browser) adds packs beside it. A real directory already at .tools/<name>
# (the layout before the shared store) is renamed aside, never deleted. A macOS bundle install (the JDK's
# Contents/Home) is linked at that home, so consumers find bin/ directly under .tools/<name>.
set -eu
cd "$(dirname "$0")/.."
mise="$1"; shift
mkdir -p .tools
for pair in "$@"; do
  name="${pair%%=*}"; tool="${pair#*=}"
  if ! target="$("$mise" where "$tool" 2>/dev/null)" || [ ! -d "$target" ]; then continue; fi
  target="$(cd "$target" && pwd -P)"; install="$target"
  [ ! -d "$target/Contents/Home" ] || target="$target/Contents/Home"
  if [ -d ".tools/$name" ] && [ ! -L ".tools/$name" ]; then
    mv ".tools/$name" ".tools/$name.local-$(date +%s)"
    echo "Moved the checkout-local .tools/$name aside; it can be deleted." >&2
  fi
  if [ "$(readlink ".tools/$name" 2>/dev/null)" != "$target" ]; then ln -sfn "$target" ".tools/$name"; fi
  case "$name" in dotnet) ;; *) [ ! -w "$install" ] || chmod -R a-w "$install" ;; esac
done
