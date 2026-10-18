$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Invoke-Native([string] $Executable, [string[]] $Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
}

# The Windows runtime identifier of a native build: the requested one, or the host's architecture when none is given.
function Resolve-WindowsRuntime([string] $RuntimeIdentifier) {
    if ($RuntimeIdentifier -eq '') {
        $RuntimeIdentifier = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) { 'win-arm64' } else { 'win-x64' }
    }
    if ($RuntimeIdentifier -notin @('win-x64', 'win-arm64')) { throw "Windows natives support win-x64 and win-arm64 only, not $RuntimeIdentifier." }
    return $RuntimeIdentifier
}

function Get-ZigWindowsTarget([string] $RuntimeIdentifier) {
    if ($RuntimeIdentifier -eq 'win-arm64') { return 'aarch64-windows-gnu' }
    return 'x86_64-windows-gnu'
}

function Get-VerifiedArchive([string] $Url, [string] $Archive, [string] $Hash, [string] $Destination) {
    if (!(Test-Path $Archive)) {
        Invoke-WebRequest -UseBasicParsing $Url -OutFile "$Archive.partial"
        Move-Item "$Archive.partial" $Archive -Force
    }
    if ((Get-FileHash $Archive -Algorithm SHA256).Hash -ne $Hash) { throw "Checksum mismatch: $Archive" }
    if (!(Test-Path $Destination)) {
        $partial = "$Destination.partial"
        if (Test-Path $partial) { Remove-Item $partial -Recurse -Force }
        New-Item -ItemType Directory $partial -Force | Out-Null
        Invoke-Native tar @('-xzf', $Archive, '-C', $partial, '--strip-components=1')
        Move-Item $partial $Destination
    }
}

function Get-WindowsZig {
    $toolRoot = Join-Path $PSScriptRoot '../.tools/windows-native'
    New-Item -ItemType Directory $toolRoot -Force | Out-Null
    $toolLock = Enter-NativeBuild $toolRoot
    try {
        # Zig cross-compiles, so the compiler matches the host and the target is chosen per build.
        $hostArch = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) { 'aarch64' } else { 'x86_64' }
        $hash = if ($hostArch -eq 'aarch64') { 'aee38316ee4111717900f45dd3130145c39289e105541d737eb8c5ed653c78ef' } else { '68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e' }
        $name = "zig-$hostArch-windows-0.16.0"
        $directory = Join-Path $toolRoot $name
        $executable = Join-Path $directory 'zig.exe'
        if (!(Test-Path $executable)) {
            $archive = Join-Path $toolRoot "$name.zip"
            if (!(Test-Path $archive)) {
                Invoke-WebRequest -UseBasicParsing "https://ziglang.org/download/0.16.0/$name.zip" -OutFile "$archive.partial"
                Move-Item "$archive.partial" $archive -Force
            }
            if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $hash) { throw 'Zig checksum mismatch' }
            $partial = Join-Path $toolRoot 'zig.partial'
            if (Test-Path $partial) { Remove-Item $partial -Recurse -Force }
            Expand-Archive $archive $partial
            Move-Item (Join-Path $partial $name) $directory
            Remove-Item $partial -Recurse -Force
        }
        return [IO.Path]::GetFullPath($executable)
    }
    finally { $toolLock.Dispose() }
}

function Enter-NativeBuild([string] $Cache) {
    New-Item -ItemType Directory $Cache -Force | Out-Null
    while ($true) {
        try { return [IO.File]::Open((Join-Path $Cache 'windows-build.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException] { Start-Sleep -Seconds 1 }
    }
}

function Get-NativeFingerprint([string[]] $Paths) {
    return (($Paths | Sort-Object | ForEach-Object { (Get-FileHash $_ -Algorithm SHA256).Hash }) -join '')
}
