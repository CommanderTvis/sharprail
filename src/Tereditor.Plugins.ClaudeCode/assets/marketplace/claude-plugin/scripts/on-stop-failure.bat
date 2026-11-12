@echo off
rem Windows counterpart of on-stop-failure.sh: reports the stop_failure status event (see hook.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0hook.ps1" stop_failure
exit /b 0
