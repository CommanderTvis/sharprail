param([string] $RuntimeIdentifier = '', [string] $Cache = "$PSScriptRoot/obj/native")
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../../scripts/windows-native.ps1"
$RuntimeIdentifier = Resolve-WindowsRuntime $RuntimeIdentifier
$target = Get-ZigWindowsTarget $RuntimeIdentifier
$Cache = [IO.Path]::GetFullPath($Cache)
$buildLock = Enter-NativeBuild $Cache
try {
    $out = Join-Path $Cache $RuntimeIdentifier
    New-Item -ItemType Directory $out -Force | Out-Null
    $native = @(Get-ChildItem "$PSScriptRoot/Native" -File | Where-Object { $_.Extension -in @('.h', '.cxx', '.patch') } | ForEach-Object { $_.FullName })
    $fingerprint = $target + (Get-NativeFingerprint ($native + @($PSCommandPath, "$PSScriptRoot/../../scripts/windows-native.ps1")))
    $stamp = Join-Path $out 'fingerprint'
    $library = Join-Path $out 'SharpRailScintillaNative.dll'
    if ((Test-Path $library) -and (Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $fingerprint) { return }
    $zig = Get-WindowsZig
    Get-VerifiedArchive 'https://www.scintilla.org/scintilla567.tgz' (Join-Path $Cache 'scintilla567.tgz') `
        '19e4476706750056f86ca70974810e4a5b08e73477e13f7d9fae78243606f6a7' (Join-Path $Cache 'scintilla-5.6.7')
    Get-VerifiedArchive 'https://github.com/Tehreer/SheenBidi/archive/refs/tags/v3.0.0.tar.gz' (Join-Path $Cache 'sheenbidi-3.0.0.tgz') `
        '86c56014034739ba39a24c23eb00323b0bf6f737354f665786015fca842af786' (Join-Path $Cache 'sheenbidi-3.0.0')
    $source = Join-Path $Cache 'windows-scintilla-patched'
    if (Test-Path $source) { Remove-Item $source -Recurse -Force }
    Copy-Item (Join-Path $Cache 'scintilla-5.6.7') $source -Recurse
    Push-Location $source
    try { Invoke-Native git @('apply', '--no-index', '--unsafe-paths', "$PSScriptRoot/Native/EditView.patch") }
    finally { Pop-Location }
    $bidi = Join-Path $Cache 'sheenbidi-3.0.0'
    $flags = @('-target', $target, '-O2', '-DNDEBUG', '-fvisibility=hidden')
    $objects = @((Join-Path $out 'SheenBidi.o'))
    Invoke-Native $zig (@('cc', '-std=c99') + $flags + @('-DSB_CONFIG_UNITY', '-I', "$bidi/Headers", '-I', "$bidi/Source", '-c', "$bidi/Source/SheenBidi.c", '-o', $objects[0]))
    $files = @(Get-ChildItem "$source/src/*.cxx" | Where-Object { $_.Name -notin @('AutoComplete.cxx', 'CallTip.cxx', 'ScintillaBase.cxx') }) + @(Get-ChildItem "$PSScriptRoot/Native/*.cxx")
    foreach ($file in $files) {
        $name = $file.BaseName
        if ($file.DirectoryName -eq (Join-Path $PSScriptRoot 'Native')) { $name = "Bridge$name" }
        $object = Join-Path $out "$name.o"
        Invoke-Native $zig (@('c++', '-std=c++17') + $flags + @('-I', "$source/include", '-I', "$source/src", '-I', "$bidi/Headers", '-c', $file.FullName, '-o', $object))
        $objects += $object
    }
    Invoke-Native $zig (@('c++', '-target', $target, '-shared', '-static-libstdc++') + $objects + @('-o', $library))
    Set-Content $stamp $fingerprint
}
finally { $buildLock.Dispose() }
