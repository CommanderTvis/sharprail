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
    $hex = '0x{0:X8}' -f ([uint32] ($code -band 0xFFFFFFFF))
    $message = "$Zig $($Arguments -join ' ') failed with exit code $code ($hex)"
    Write-Host "::error title=Zig failed::$($message -replace '%', '%25' -replace "`r", '%0D' -replace "`n", '%0A')"
    Write-Host '--- Application Error events (newest 5) ---'
    try { Get-WinEvent -LogName Application -MaxEvents 5 -ProviderName 'Application Error' -ErrorAction Stop | Format-List TimeCreated, Message | Out-String | Write-Host }
    catch { Write-Host "No Application Error events: $($_.Exception.Message)" }
    $cache = Join-Path $Out 'zig-cache'
    if (Test-Path $cache) {
        Write-Host "--- Last 50 lines of the newest files in $cache ---"
        Get-ChildItem $cache -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in @('.txt', '.log', '.d', '') -and $_.Length -lt 1MB } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object { Write-Host "# $($_.FullName)"; Get-Content $_.FullName -Tail 50 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "$_" } }
    }
    throw $message
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
