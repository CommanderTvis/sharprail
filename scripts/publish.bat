@echo off
rem Publishes the win-x64 package into artifacts\windows (counterpart of publish.sh; the .app bundle is macOS-only).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-windows.ps1" %*
exit /b %errorlevel%
