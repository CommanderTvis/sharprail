@echo off
rem Launches only the remote host (the Windows counterpart of host.sh); scan the QR code with the Android client.
rem SHARPRAIL_CONNECT_HOST overrides the advertised hostname/IP (without a port).
setlocal
cd /d "%~dp0.."
if not exist ".tools\dotnet\dotnet.exe" call "%~dp0bootstrap.bat" || exit /b 1
set "DOTNET_ROOT=%CD%\.tools\dotnet"
if not defined SHARPRAIL_ROOT set "SHARPRAIL_ROOT=%CD%"
set "SHARPRAIL_BIND=0.0.0.0"
if not defined SHARPRAIL_PORT set "SHARPRAIL_PORT=54123"
if not defined SHARPRAIL_TOKEN for /f %%T in ('powershell -NoProfile -Command "(New-Guid).Guid + (New-Guid).Guid"') do set "SHARPRAIL_TOKEN=%%T"
if not defined SHARPRAIL_CONFIGURATION set "SHARPRAIL_CONFIGURATION=Debug"
".tools\dotnet\dotnet.exe" build src\SharpRail.Host.Remote -c %SHARPRAIL_CONFIGURATION% || exit /b 1
".tools\dotnet\dotnet.exe" run --file scripts\host-link.cs || exit /b 1
".tools\dotnet\dotnet.exe" "src\SharpRail.Host.Remote\bin\%SHARPRAIL_CONFIGURATION%\net10.0\SharpRail.Host.Remote.dll" %*
exit /b %errorlevel%
