param([string] $RuntimeIdentifier = '', [string] $Cache = "$PSScriptRoot/obj/native")
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../../scripts/windows-native.ps1"
$RuntimeIdentifier = Resolve-WindowsRuntime $RuntimeIdentifier
$target = Get-ZigWindowsTarget $RuntimeIdentifier
# $Cache is the checkout's output directory. The pinned source, the Zig cache and each finished build (results/<key>, the
# key hashing everything the build reads) live in the machine-wide cache every checkout shares, like build-native.sh's.
$Cache = [IO.Path]::GetFullPath($Cache)
$out = Join-Path $Cache $RuntimeIdentifier
New-Item -ItemType Directory $out -Force | Out-Null
$commit = $env:GHOSTTY_VT_COMMIT
if (!$commit) { throw 'Run through scripts\mise x -- (see mise.toml).' }
$fingerprint = $commit + $target + $ZigWindowsCpu + (Get-NativeFingerprint @($PSCommandPath, "$PSScriptRoot/../../scripts/windows-native.ps1", "$PSScriptRoot/Native/GhosttyVt.c", "$PSScriptRoot/Native/GhosttyClipboardStub.c"))
$stamp = Join-Path $out 'fingerprint'
$library = Join-Path $out 'GhosttyAvaloniaVt.dll'
if ((Test-Path $library) -and (Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $fingerprint) { return }
$work = Join-Path (Get-SharpRailCacheDir) 'ghostty-avalonia'
$buildLock = Enter-NativeBuild $work
try {
    $key = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($fingerprint))) -Algorithm SHA256).Hash.Substring(0, 16).ToLowerInvariant()
    $result = Join-Path $work "results/$RuntimeIdentifier-vt-$key"
    if (!(Test-Path (Join-Path $result 'GhosttyAvaloniaVt.dll'))) {
        $zig = Get-WindowsZig
        $source = Join-Path $work 'windows-vt-source'
        if ((Test-Path $source) -and (& git -C $source rev-parse HEAD).Trim() -ne $commit) { Remove-Item $source -Recurse -Force }
        if (!(Test-Path $source)) {
            $partial = "$source.partial"
            if (Test-Path $partial) { Remove-Item $partial -Recurse -Force }
            Invoke-Native git @('init', '-q', $partial)
            Invoke-Native git @('-C', $partial, 'fetch', '-q', '--depth', '1', 'https://github.com/ghostty-org/ghostty.git', $commit)
            Invoke-Native git @('-C', $partial, 'checkout', '-q', 'FETCH_HEAD')
            Move-Item $partial $source
        }
        if ((& git -C $source rev-parse HEAD).Trim() -ne $commit) { throw 'Unexpected Ghostty source revision' }
        $build = Join-Path $work $RuntimeIdentifier
        $prefix = Join-Path $build 'vt'
        $staging = "$result.partial"
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
        New-Item -ItemType Directory $staging -Force | Out-Null
        Restore-ZigDeps
        Push-Location $source
        try { Invoke-Zig $zig $RuntimeIdentifier $build @('build', '--cache-dir', (Join-Path $build 'zig-cache'), '--prefix', $prefix, '-Demit-lib-vt', '-Doptimize=ReleaseFast', "-Dtarget=$target", "-Dcpu=$ZigWindowsCpu") }
        finally { Pop-Location }
        Invoke-Zig $zig $RuntimeIdentifier $build @('cc', '-target', $target, "-mcpu=$ZigWindowsCpu", '-shared', '-O2', '-Wall', '-Wextra', '-Werror', '-DGHOSTTY_STATIC',
            '-I', (Join-Path $prefix 'include'), "$PSScriptRoot/Native/GhosttyVt.c", "$PSScriptRoot/Native/GhosttyClipboardStub.c",
            (Join-Path $prefix 'lib/ghostty-vt-static.lib'), '-o', (Join-Path $staging 'GhosttyAvaloniaVt.dll'))
        if (Test-Path $result) { Remove-Item $result -Recurse -Force }
        Move-Item $staging $result
    }
    Copy-Item (Join-Path $result 'GhosttyAvaloniaVt.dll') "$library.partial" -Force
    Move-Item "$library.partial" $library -Force
    Set-Content $stamp $fingerprint
}
finally { $buildLock.Dispose() }
