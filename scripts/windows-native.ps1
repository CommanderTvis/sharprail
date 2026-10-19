$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Invoke-Native([string] $Executable, [string[]] $Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
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

# Runs Zig for one native build: its own global and local caches under $Out (never shared with a parallel build), the
# build's output and Zig's own diagnostics in the log, and one retry with the caches wiped after an access violation
# (0xC0000005), which Zig 0.16 raised on the Windows ARM64 runner. On win-arm64 the Zig runs of all native builds take a
# machine-wide mutex, so the Scintilla and Ghostty builds MSBuild starts in parallel do not run Zig at the same time.
$script:ZigDiagnosed = $false
function Invoke-Zig([string] $Zig, [string] $RuntimeIdentifier, [string] $Out, [string[]] $Arguments) {
    $globalCache = Join-Path $Out 'zig-global-cache'
    $localCache = Join-Path $Out 'zig-local-cache'
    $mutex = $null
    if ($RuntimeIdentifier -eq 'win-arm64') {
        $mutex = New-Object Threading.Mutex($false, 'Global\SharpRailWindowsZig')
        try { [void] $mutex.WaitOne() } catch [Threading.AbandonedMutexException] { }
    }
    $savedGlobal = $env:ZIG_GLOBAL_CACHE_DIR
    $savedLocal = $env:ZIG_LOCAL_CACHE_DIR
    try {
        New-Item -ItemType Directory $globalCache, $localCache -Force | Out-Null
        $env:ZIG_GLOBAL_CACHE_DIR = $globalCache
        $env:ZIG_LOCAL_CACHE_DIR = $localCache
        if (!$script:ZigDiagnosed) {
            $script:ZigDiagnosed = $true
            Write-Host "Zig host: OS $([Runtime.InteropServices.RuntimeInformation]::OSArchitecture), process $([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture), target $RuntimeIdentifier"
            foreach ($diagnostic in @(@('version'), @('env'))) {
                Write-Host "> $Zig $($diagnostic -join ' ')"
                $saved = $ErrorActionPreference
                $ErrorActionPreference = 'Continue'
                try { & $Zig @diagnostic 2>&1 | ForEach-Object { Write-Host "$_" } } finally { $ErrorActionPreference = $saved }
            }
        }
        for ($attempt = 1; $true; $attempt++) {
            Write-Host "> $Zig $($Arguments -join ' ')"
            $saved = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try { & $Zig @Arguments 2>&1 | ForEach-Object { Write-Host "$_" } } finally { $ErrorActionPreference = $saved }
            $code = $LASTEXITCODE
            if ($code -eq 0) { return }
            if ($code -eq -1073741819 -and $attempt -eq 1) {
                Write-Host "Zig crashed with an access violation (exit code $code); wiping its caches and retrying once."
                foreach ($dir in @($globalCache, $localCache, (Join-Path $Out 'zig-cache'))) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue } }
                New-Item -ItemType Directory $globalCache, $localCache -Force | Out-Null
                continue
            }
            throw "$Zig $($Arguments -join ' ') failed with exit code $code"
        }
    }
    finally {
        $env:ZIG_GLOBAL_CACHE_DIR = $savedGlobal
        $env:ZIG_LOCAL_CACHE_DIR = $savedLocal
        if ($mutex) { $mutex.ReleaseMutex(); $mutex.Dispose() }
    }
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
