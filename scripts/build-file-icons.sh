#!/bin/sh
# Writes the file-type icon assets; the kit's BuildFileIcons target decides when it runs.
set -eu
cd "$(dirname "$0")/.."
"${DOTNET_HOST_PATH:-dotnet}" run scripts/generate-file-icons.cs -- --assets-only
