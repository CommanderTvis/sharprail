@echo off
rem Builds, publishes, checks or runs the browser client (`scripts\mise run browser [build|run|publish|check]`):
setlocal EnableDelayedExpansion
cd /d "%~dp0..\.."
set "ROOT=%CD%"
set "DOTNET=%ROOT%\.tools\dotnet\dotnet.exe"
if not exist ".tools\dotnet\packs\Microsoft.NETCore.App.Runtime.Mono.multithread.browser-wasm" (
  "%DOTNET%" workload install wasm-experimental --skip-manifest-update || exit /b 1
)
if not defined SHARPRAIL_CONFIGURATION set "SHARPRAIL_CONFIGURATION=Release"
set "CONFIG=%SHARPRAIL_CONFIGURATION%"
set "ACTION=%~1"
if "%ACTION%"=="" set "ACTION=build"

if /i "%ACTION%"=="build" (
  "%DOTNET%" build src\SharpRail.Browser -c %CONFIG% -m:2
  exit /b !errorlevel!
)
if /i "%ACTION%"=="publish" (
  "%DOTNET%" publish src\SharpRail.Browser -c %CONFIG% -m:2 -p:WasmAppDir="%ROOT%\artifacts\browser"
  exit /b !errorlevel!
)
if /i "%ACTION%"=="check" (
  "%DOTNET%" build src\SharpRail.Host.Remote -c %CONFIG% -m:2 || exit /b 1
  "%DOTNET%" build src\SharpRail.Browser -c %CONFIG% -m:2 -p:BrowserChecks=true || exit /b 1
  set "PLAYWRIGHT_BROWSERS_PATH=%ROOT%\.tools\playwright"
  pushd src\SharpRail.Browser
  call npm exec -- playwright install chromium
  set "INSTALLED=!errorlevel!"
  popd
  if not "!INSTALLED!"=="0" exit /b 1
  node tests\SharpRail.Browser.Checks\check.mjs
  exit /b !errorlevel!
)
if /i "%ACTION%"=="run" (
  if not defined SHARPRAIL_TOKEN (
    echo Set SHARPRAIL_TOKEN to the host session token. 1>&2
    exit /b 1
  )
  "%DOTNET%" build src\SharpRail.Browser -c %CONFIG% -m:2 || exit /b 1
  "%DOTNET%" build src\SharpRail.Host.Remote -c %CONFIG% -m:2 || exit /b 1
  if not defined SHARPRAIL_PORT set "SHARPRAIL_PORT=54123"
  set "SHARPRAIL_BROWSER_ROOT=%ROOT%\src\SharpRail.Browser\bin\%CONFIG%\net10.0-browser\AppBundle"
  "%DOTNET%" run --no-build --project src\SharpRail.Host.Remote -c %CONFIG%
  exit /b !errorlevel!
)
echo Usage: scripts\browser.bat [build^|run^|publish^|check] 1>&2
exit /b 1
