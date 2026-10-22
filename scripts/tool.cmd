@echo off
rem Counterpart of scripts/tool: scripts\tool <name> prints the directory mise keeps a pinned tool in (dotnet or zig-windows),
rem installing it first when it is missing.
setlocal DisableDelayedExpansion
if "%~1"=="dotnet" (set "spec=http:dotnet") else if "%~1"=="zig-windows" (set "spec=http:zig-windows") else (echo scripts\tool: unknown tool %~1 1>&2 & exit /b 2)
set "dir="
for /f "usebackq delims=" %%d in (`call "%~dp0mise.cmd" where %spec% 2^>nul`) do set "dir=%%d"
if not defined dir call "%~dp0mise.cmd" install %spec% 1>&2
if not defined dir for /f "usebackq delims=" %%d in (`call "%~dp0mise.cmd" where %spec%`) do set "dir=%%d"
echo %dir%
