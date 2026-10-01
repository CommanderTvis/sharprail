#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
bench=benchmarks/ghostty
clang -O2 -Wall -Wextra "$bench/native/workload.c" -o "$bench/native/workload"
clang -O2 -dynamiclib -fobjc-arc -Wall -Wextra -Wno-unused-parameter -mmacosx-version-min=14.0 \
  "$bench/native/Capture.m" -framework AppKit -framework ScreenCaptureKit -framework CoreMedia -framework CoreVideo \
  -o "$bench/native/libBenchCapture.dylib"
clang -O2 -dynamiclib -fobjc-arc -mmacosx-version-min=14.0 "$bench/native/MetalTrace.m" \
  -framework Foundation -framework Metal -framework IOSurface -o "$bench/native/libMetalTrace.dylib"
clang -O2 -dynamiclib "$bench/native/IOTrace.c" -framework IOKit -o "$bench/native/libIOTrace.dylib"
for project in presentation/Bench memory/Memory attribution/Attribution; do
  .tools/dotnet/dotnet build "$bench/$project.csproj" -c Release
done
