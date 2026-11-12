@echo off
rem Windows counterpart of on-model-switch.sh: reports the model_switch status event (see hook.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0hook.ps1" model_switch
exit /b 0
