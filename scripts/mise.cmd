@echo off
rem Counterpart of scripts/mise: a mise already on the machine runs as is, with the user's own configuration and mise's
rem default data directory; without one, the pinned mise runs against the tool store shared by all checkouts.
setlocal DisableDelayedExpansion
where mise >nul 2>nul
if not errorlevel 1 (
  set "DOTNET_ROOT="
  set "DOTNET_ROOT_ARM64="
  set "DOTNET_ROOT_X64="
  set "MISE_TRUSTED_CONFIG_PATHS=%~dp0..;%MISE_TRUSTED_CONFIG_PATHS%"
  mise %*
  exit /b %errorlevel%
)
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
