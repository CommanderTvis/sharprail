@echo off
rem Windows counterpart of on-post-tool-use.sh: reports the tool_complete status event (see hook.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0hook.ps1" tool_complete
exit /b 0
