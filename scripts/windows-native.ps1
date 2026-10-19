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

# The CPU of every Windows native build. Zig builds for the detected CPU when the target is the host's own architecture and OS,
# which tied the shipped libraries to the build machine's features and, on the Windows ARM64 runner, made Zig 0.16 pass
# a mangled feature list to Clang (unknown argument '-fuse-crypto-eor'). Baseline is the same everywhere.
$ZigWindowsCpu = 'baseline'

# The Zig that `scripts/mise run bootstrap` installs (http:zig-windows in mise.toml) and links as .tools\zig-windows.
# Zig cross-compiles, so it matches the host and the target is chosen per build.
function Get-WindowsZig {
    $zig = Join-Path $PSScriptRoot '../.tools/zig-windows/zig.exe'
    if (!(Test-Path $zig)) { throw 'No Zig in .tools; run scripts\mise run bootstrap.' }
    return [IO.Path]::GetFullPath($zig)
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
