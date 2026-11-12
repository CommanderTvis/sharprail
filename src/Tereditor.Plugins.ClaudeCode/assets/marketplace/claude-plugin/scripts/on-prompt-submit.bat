@echo off
rem Windows counterpart of on-prompt-submit.sh: reports the prompt_submit status event (see hook.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0hook.ps1" prompt_submit
exit /b 0
