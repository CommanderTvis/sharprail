#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
mkdir -p .tools
curl -fsSL https://dot.net/v1/dotnet-install.sh -o .tools/dotnet-install.sh
bash .tools/dotnet-install.sh --version 10.0.401 --install-dir .tools/dotnet --no-path
