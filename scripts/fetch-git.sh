#!/bin/sh
# Checks out one pinned commit of a Git repository into a new directory: fetch-git.sh <directory> <url> <commit>.
# A commit id is its own checksum, so this is the fetcher for sources pinned by revision; archives are pinned by
# checksum in mise.toml and installed by mise instead.
set -eu
dir="$1"; url="$2"; commit="$3"
git init -q "$dir"
git -C "$dir" fetch -q --depth 1 "$url" "$commit"
git -C "$dir" checkout -q FETCH_HEAD
[ "$(git -C "$dir" rev-parse HEAD)" = "$commit" ] || { echo "Unexpected revision of $url" >&2; exit 1; }
