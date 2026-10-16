@echo off
rem Counterpart of build-file-icons.sh.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-file-icons.ps1" %*
exit /b %errorlevel%
