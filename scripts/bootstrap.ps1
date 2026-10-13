$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/..")
$tools = Join-Path $root '.tools'
New-Item -ItemType Directory $tools -Force | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$installer = Join-Path $tools 'dotnet-install.ps1'
Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
$version = (Get-Content (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version
& $installer -Version $version -Architecture x64 -InstallDir (Join-Path $tools 'dotnet') -NoPath
if (!$?) { throw 'Installing the checkout SDK failed.' }
