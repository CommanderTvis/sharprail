$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/windows-native.ps1"
$root = [IO.Path]::GetFullPath("$PSScriptRoot/..")
$cache = Join-Path $root '.tools/file-icons'
$buildLock = Enter-NativeBuild $cache
try {
    $fingerprint = Get-NativeFingerprint @($PSCommandPath, "$PSScriptRoot/generate-file-icons.cs", "$PSScriptRoot/../src/SharpRail.Plugins.UI.Kit/FileIcons/FileIconTable.g.cs")
    $stamp = Join-Path $cache 'windows.fingerprint'
    if ((Test-Path "$cache/assets/file-icons/file.svg") -and (Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $fingerprint) { return }
    $dotnet = $env:DOTNET_HOST_PATH
    if (!$dotnet) { $dotnet = Join-Path $root '.tools/dotnet/dotnet.exe' }
    Push-Location $root
    try { Invoke-Native $dotnet @('run', "$PSScriptRoot/generate-file-icons.cs", '--', '--assets-only') }
    finally { Pop-Location }
    Set-Content $stamp $fingerprint
}
finally { $buildLock.Dispose() }
