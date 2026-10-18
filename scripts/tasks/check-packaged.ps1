# `scripts\mise run check:packaged -RuntimeIdentifier win-arm64 [checks arguments]`: runs the check gate from the
# published Windows package (counterpart of check-packaged.sh without the macOS bundle).
param([ValidateSet('win-x64', 'win-arm64')][string] $RuntimeIdentifier = 'win-x64', [Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$directory = if ($RuntimeIdentifier -eq 'win-x64') { 'windows' } else { 'windows-arm64' }
$checks = Join-Path $root "artifacts/$directory/checks/SharpRail.Checks.exe"
if (!(Test-Path $checks)) { throw "No packaged app to check: run scripts\mise run package:windows $RuntimeIdentifier first." }
$env:SHARPRAIL_REQUIRE_R2R = '1'
& $checks @Arguments
exit $LASTEXITCODE
