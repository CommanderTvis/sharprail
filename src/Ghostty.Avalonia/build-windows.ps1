param([string] $RuntimeIdentifier = '', [string] $Cache = "$PSScriptRoot/obj/native")
$ErrorActionPreference = 'Stop'
if ($RuntimeIdentifier -notin @('', 'win-x64')) { throw 'Windows terminals support win-x64 only.' }
. "$PSScriptRoot/../../scripts/windows-native.ps1"
$Cache = [IO.Path]::GetFullPath($Cache)
$buildLock = Enter-NativeBuild $Cache
try {
    $out = Join-Path $Cache 'win-x64'
    New-Item -ItemType Directory $out -Force | Out-Null
    $commit = '59c2dc032aba42aa5064bf206cc27286add3e9d8'
    $fingerprint = $commit + (Get-NativeFingerprint @($PSCommandPath, "$PSScriptRoot/../../scripts/windows-native.ps1", "$PSScriptRoot/Native/GhosttyVt.c", "$PSScriptRoot/Native/GhosttyClipboardStub.c"))
    $stamp = Join-Path $out 'fingerprint'
    $library = Join-Path $out 'GhosttyAvaloniaVt.dll'
    if ((Test-Path $library) -and (Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $fingerprint) { return }
    $zig = Get-WindowsZig
    $source = Join-Path $Cache 'windows-vt-source'
    if (!(Test-Path $source)) {
        $partial = "$source.partial"
        if (Test-Path $partial) { Remove-Item $partial -Recurse -Force }
        Invoke-Native git @('init', '-q', $partial)
        Invoke-Native git @('-C', $partial, 'fetch', '-q', '--depth', '1', 'https://github.com/ghostty-org/ghostty.git', $commit)
        Invoke-Native git @('-C', $partial, 'checkout', '-q', 'FETCH_HEAD')
        Move-Item $partial $source
    }
    if ((& git -C $source rev-parse HEAD).Trim() -ne $commit) { throw 'Unexpected Ghostty source revision' }
    $prefix = Join-Path $out 'vt'
    Push-Location $source
    try { Invoke-Native $zig @('build', '--cache-dir', (Join-Path $out 'zig-cache'), '--prefix', $prefix, '-Demit-lib-vt', '-Doptimize=ReleaseFast', '-Dtarget=x86_64-windows-gnu') }
    finally { Pop-Location }
    Invoke-Native $zig @('cc', '-target', 'x86_64-windows-gnu', '-shared', '-O2', '-Wall', '-Wextra', '-Werror', '-DGHOSTTY_STATIC',
        '-I', (Join-Path $prefix 'include'), "$PSScriptRoot/Native/GhosttyVt.c", "$PSScriptRoot/Native/GhosttyClipboardStub.c",
        (Join-Path $prefix 'lib/ghostty-vt-static.lib'), '-o', $library)
    Set-Content $stamp $fingerprint
}
finally { $buildLock.Dispose() }
