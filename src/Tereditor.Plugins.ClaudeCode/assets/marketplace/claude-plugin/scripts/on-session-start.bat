@echo off
rem Windows counterpart of on-session-start.sh: reports the session_start status event (see hook.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0hook.ps1" session_start
exit /b 0
