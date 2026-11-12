@echo off
rem Windows counterpart of on-stop.sh: reports the stop status event (see hook.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0hook.ps1" stop
exit /b 0
