$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Invoke-Native([string] $Executable, [string[]] $Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
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
        $directory = Join-Path $toolRoot 'zig-x86_64-windows-0.16.0'
        $executable = Join-Path $directory 'zig.exe'
        if (!(Test-Path $executable)) {
            $archive = Join-Path $toolRoot 'zig-0.16.0.zip'
            if (!(Test-Path $archive)) {
                Invoke-WebRequest -UseBasicParsing 'https://ziglang.org/download/0.16.0/zig-x86_64-windows-0.16.0.zip' -OutFile "$archive.partial"
                Move-Item "$archive.partial" $archive -Force
            }
            if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne '68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e') { throw 'Zig checksum mismatch' }
            $partial = Join-Path $toolRoot 'zig.partial'
            if (Test-Path $partial) { Remove-Item $partial -Recurse -Force }
            Expand-Archive $archive $partial
            Move-Item (Join-Path $partial 'zig-x86_64-windows-0.16.0') $directory
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
