@echo off
rem Builds SharpRail from source and starts it (`scripts\mise run dev`; the Windows counterpart of dev.sh).
rem The app is a GUI executable, so it is started detached and no console window stays open for it.
rem SHARPRAIL_PROFILE keeps a separate profile; SHARPRAIL_CONFIGURATION defaults to Debug. Arguments go to the app.
setlocal
cd /d "%~dp0..\.."
set "DOTNET_ROOT=%CD%\.tools\dotnet"
if not defined SHARPRAIL_ROOT set "SHARPRAIL_ROOT=%CD%"
if not defined SHARPRAIL_CONFIGURATION set "SHARPRAIL_CONFIGURATION=Debug"
rem Run from inside a Claude Code session, the app must not carry that session's identity (or its NO_COLOR,
rem which would otherwise switch colour off in every terminal tab) into its terminals.
if defined CLAUDECODE set "NO_COLOR="
for %%V in (CLAUDECODE CLAUDE_CODE_CHILD_SESSION CLAUDE_CODE_ENTRYPOINT CLAUDE_CODE_EXECPATH CLAUDE_CODE_SESSION_ID CLAUDE_CODE_BRIDGE_SESSION_ID CLAUDE_CODE_MESSAGING_SOCKET CLAUDE_CODE_MESSAGING_TOKEN CLAUDE_CODE_SESSION_ATTENDED CLAUDE_CODE_SSE_PORT CLAUDE_PID CLAUDE_EFFORT) do set "%%V="
".tools\dotnet\dotnet.exe" build src\SharpRail.UI -c %SHARPRAIL_CONFIGURATION% || exit /b 1
start "" "src\SharpRail.UI\bin\%SHARPRAIL_CONFIGURATION%\net10.0\SharpRail.UI.exe" %*
