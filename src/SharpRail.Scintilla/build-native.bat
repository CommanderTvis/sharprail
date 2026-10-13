@echo off
rem Builds the Windows native library (counterpart of build-native.sh); arguments: optional RID and cache directory.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-windows.ps1" %*
exit /b %errorlevel%
