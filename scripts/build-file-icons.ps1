$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/windows-native.ps1"
$root = [IO.Path]::GetFullPath("$PSScriptRoot/..")
New-Item -ItemType Directory (Join-Path $root '.tools/file-icons') -Force | Out-Null
$dotnet = $env:DOTNET_HOST_PATH
if (!$dotnet) { $dotnet = Join-Path $root '.tools/dotnet/dotnet.exe' }
Push-Location $root
try { Invoke-Native $dotnet @('run', "$PSScriptRoot/generate-file-icons.cs", '--', '--assets-only') }
finally { Pop-Location }
