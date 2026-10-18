@echo off
rem Counterpart of scripts/mise: the pinned mise against the tool store shared by all checkouts.
setlocal DisableDelayedExpansion
if not defined SHARPRAIL_TOOLS set "SHARPRAIL_TOOLS=%USERPROFILE%\.sharprail\tools"
if not exist "%SHARPRAIL_TOOLS%" mkdir "%SHARPRAIL_TOOLS%"
set "DOTNET_ROOT="
set "DOTNET_ROOT_ARM64="
set "DOTNET_ROOT_X64="
set "MISE_DATA_DIR=%SHARPRAIL_TOOLS%"
set "MISE_CACHE_DIR=%SHARPRAIL_TOOLS%\cache"
set "MISE_STATE_DIR=%SHARPRAIL_TOOLS%\state"
set "MISE_TRUSTED_CONFIG_PATHS=%~dp0..;%MISE_TRUSTED_CONFIG_PATHS%"
set "MISE_IGNORED_CONFIG_PATHS=%USERPROFILE%\.config\mise;%MISE_IGNORED_CONFIG_PATHS%"
call "%~dp0mise-pinned.cmd" %*
exit /b %errorlevel%
