@echo off
rem Counterpart of scripts/mise: a mise already on the machine runs as is; without one the pinned mise runs. Either way
rem tools install into mise's own data directory, which every checkout and worktree of the user shares.
setlocal DisableDelayedExpansion
set "DOTNET_ROOT="
set "DOTNET_ROOT_ARM64="
set "DOTNET_ROOT_X64="
set "MISE_TRUSTED_CONFIG_PATHS=%~dp0..;%MISE_TRUSTED_CONFIG_PATHS%"
where mise >nul 2>nul
if not errorlevel 1 (
  mise %*
  exit /b %errorlevel%
)
call "%~dp0mise-pinned.cmd" %*
exit /b %errorlevel%
