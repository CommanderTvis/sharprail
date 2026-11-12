@echo off
rem Windows counterpart of on-permission-request.sh: reports the permission_request status event (see hook.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0hook.ps1" permission_request
exit /b 0
