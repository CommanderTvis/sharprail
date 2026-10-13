@echo off
rem check-terminal.sh runs a macOS AppKit/Metal probe; the Skia terminal is covered by the checks executable.
echo The native terminal probe needs macOS. Run: .tools\dotnet\dotnet.exe run --project tests\SharpRail.Checks -c Release -- --terminals 1>&2
exit /b 1
