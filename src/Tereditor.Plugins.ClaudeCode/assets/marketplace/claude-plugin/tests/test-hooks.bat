@echo off
rem Windows counterpart of test-hooks.sh. That test drives the bash hook scripts with a fake curl, so it needs
rem bash (Git for Windows), jq and curl on PATH; the Windows hook.ps1 has no separate test yet.
where bash >nul 2>nul || (echo test-hooks needs bash from Git for Windows on PATH. 1>&2 & exit /b 1)
bash "%~dp0test-hooks.sh" %*
