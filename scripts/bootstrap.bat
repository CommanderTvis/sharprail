@echo off
rem Installs the checkout SDK into .tools\dotnet (counterpart of bootstrap.sh).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bootstrap.ps1" %*
exit /b %errorlevel%
