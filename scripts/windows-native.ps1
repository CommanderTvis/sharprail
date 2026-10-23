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

# The Zig that mise installs (http:zig-windows in mise.toml); scripts\tool.cmd prints where it is.
# Zig cross-compiles, so it matches the host and the target is chosen per build.
function Get-WindowsZig {
    $zig = Join-Path (& (Join-Path $PSScriptRoot 'tool.cmd') zig-windows) 'zig.exe'
    if (!(Test-Path $zig)) { throw "No Zig at $zig." }
    return [IO.Path]::GetFullPath($zig)
}

# Runs Zig for one native build and keeps what a crash needs on the run page: the host, Zig's version and env, each
# command line, its output, the exit code in hex and, on failure, the Windows crash records and the tail of the build's
# Zig cache. It changes no behaviour: no retry, lock or cache redirection.
$script:ZigDiagnosed = $false
function Invoke-Zig([string] $Zig, [string] $RuntimeIdentifier, [string] $Out, [string[]] $Arguments) {
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
    Write-Host "> $Zig $($Arguments -join ' ')"
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Zig @Arguments 2>&1 | ForEach-Object { Write-Host "$_" } } finally { $ErrorActionPreference = $saved }
    $code = $LASTEXITCODE
    if ($code -eq 0) { return }
    $hex = '0x{0:X8}' -f [int] $code
    $message = "$Zig $($Arguments -join ' ') failed with exit code $code ($hex)"
    Write-Host "::error title=Zig failed::$($message -replace '%', '%25' -replace "`r", '%0D' -replace "`n", '%0A')"
    Write-Host '--- Application Error events (newest 5) ---'
    try { Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Application Error' } -MaxEvents 5 -ErrorAction Stop | Format-List TimeCreated, Message | Out-String | Write-Host }
    catch { Write-Host "No Application Error events: $($_.Exception.Message)" }
    $cache = Join-Path $Out 'zig-cache'
    if (Test-Path $cache) {
        Write-Host "--- Last 50 lines of the newest files in $cache ---"
        Get-ChildItem $cache -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in @('.txt', '.log', '.d', '') -and $_.Length -lt 1MB } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object { Write-Host "# $($_.FullName)"; Get-Content $_.FullName -Tail 50 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "$_" } }
    }
    throw $message
}

# The machine-wide build cache every checkout shares: SHARPRAIL_CACHE_DIR from mise.toml, else the same default.
function Get-SharpRailCacheDir {
    if ($env:SHARPRAIL_CACHE_DIR) { return $env:SHARPRAIL_CACHE_DIR }
    return Join-Path $env:LOCALAPPDATA 'sharprail\cache'
}

function Enter-NativeBuild([string] $Cache) {
    New-Item -ItemType Directory $Cache -Force | Out-Null
    while ($true) {
        try { return [IO.File]::Open((Join-Path $Cache 'windows-build.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException] { Start-Sleep -Seconds 1 }
    }
}

# Restores the Zig packages hosted outside GitHub and deps.files.ghostty.org from the mirror pinned in scripts/zig-deps.lock (see zig-deps.sh,
# which does the same for the sh builds) so the build needs not codeberg.org. Only absent
# packages are restored; a missing or corrupt mirror falls back to the upstream URLs and fails naming an unreachable one.
function Restore-ZigDeps {
    if ($env:ZIG_DEPS_NO_RESTORE) { return }
    $lock = @{ pkg = @() }
    foreach ($line in Get-Content (Join-Path $PSScriptRoot 'zig-deps.lock')) {
        $f = $line -split ' '
        if ($f[0] -eq 'pkg') { $lock.pkg += , $f } elseif ($f[0] -ne '#' -and $f.Count -ge 2 -and !$line.StartsWith('#')) { $lock[$f[0]] = $f[1] }
    }
    $cache = if ($env:ZIG_GLOBAL_CACHE_DIR) { $env:ZIG_GLOBAL_CACHE_DIR } else { Join-Path $env:LOCALAPPDATA 'zig' }
    $p = Join-Path $cache 'p'
    New-Item -ItemType Directory $p -Force | Out-Null
    $missing = @($lock.pkg | Where-Object { $_[2].Contains('tgz') -and !(Test-Path (Join-Path $p "$($_[1]).tar.gz")) })
    if (!$missing) { return }
    $archive = Join-Path $cache $lock.asset
    $ok = (Test-Path $archive) -and (Get-FileHash $archive -Algorithm SHA256).Hash -eq $lock.sha256
    if (!$ok) {
        try {
            for ($attempt = 1; !$ok -and $attempt -le 4; $attempt++) {
                try { Invoke-WebRequest "https://github.com/$($lock.repo)/releases/download/$($lock.tag)/$($lock.asset)" -OutFile "$archive.partial" -TimeoutSec 120 } catch { Start-Sleep -Seconds (2 * $attempt); continue }
                $ok = (Get-FileHash "$archive.partial" -Algorithm SHA256).Hash -eq $lock.sha256
            }
        } finally { if ($ok) { Move-Item "$archive.partial" $archive -Force } elseif (Test-Path "$archive.partial") { Remove-Item "$archive.partial" -Force } }
    }
    if ($ok) {
        $tmp = Join-Path $cache "zig-deps-extract.$PID"
        New-Item -ItemType Directory $tmp -Force | Out-Null
        try {
            Invoke-Native tar @('-xzf', $archive, '-C', $tmp)
            foreach ($m in $missing) {
                $name = "$($m[1]).tar.gz"
                if (!(Test-Path (Join-Path $p $name))) { Move-Item (Join-Path $tmp "p/$name") (Join-Path $p $name) -ErrorAction SilentlyContinue }
            }
        } finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
        Write-Host "Restored Zig packages from $($lock.asset) into $p."
        return
    }
    Write-Host "::warning title=Zig dependency mirror unavailable::$($lock.asset) (release $($lock.tag) of $($lock.repo)) could not be downloaded or failed its SHA-256 check; Zig will fetch the packages from their upstream URLs."
    $failed = $false
    foreach ($m in $missing) {
        $url = $m[3]
        $probe = if ($url.StartsWith('git+')) { ($url.Substring(4) -split '#')[0] + '/info/refs?service=git-upload-pack' } else { $url }
        try { Invoke-WebRequest $probe -Headers @{ Range = 'bytes=0-0' } -UseBasicParsing -TimeoutSec 30 | Out-Null }
        catch { Write-Host "::error title=Zig dependency unreachable::Package $($m[1]) is not mirrored and its upstream $url is unreachable. Publish the mirror (scripts/mise run zig-deps:mirror) or retry when the host is back."; $failed = $true }
    }
    if ($failed) { throw 'Zig dependencies are unavailable.' }
}

function Get-NativeFingerprint([string[]] $Paths) {
    return (($Paths | Sort-Object | ForEach-Object { (Get-FileHash $_ -Algorithm SHA256).Hash }) -join '')
}
