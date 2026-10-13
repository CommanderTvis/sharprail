@echo off
rem Runs the check gate from the published Windows package (counterpart of check-packaged.sh without the macOS bundle).
setlocal
cd /d "%~dp0.."
if not exist "artifacts\windows\checks\SharpRail.Checks.exe" (
  echo No packaged app to check: run scripts\publish.bat first. 1>&2
  exit /b 1
)
set "SHARPRAIL_REQUIRE_R2R=1"
"artifacts\windows\checks\SharpRail.Checks.exe" %*
exit /b %errorlevel%
