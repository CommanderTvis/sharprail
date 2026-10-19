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
    $fingerprint = $target + $ZigWindowsCpu + (Get-NativeFingerprint ($native + @($PSCommandPath, "$PSScriptRoot/../../scripts/windows-native.ps1")))
    $stamp = Join-Path $out 'fingerprint'
    $library = Join-Path $out 'SharpRailScintillaNative.dll'
    if ((Test-Path $library) -and (Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $fingerprint) { return }
    $zig = Get-WindowsZig
    # Pinned and checksummed in mise.toml; scripts\mise run bootstrap links them into .tools.
    $scintilla = Join-Path $PSScriptRoot '../../.tools/scintilla'
    $bidi = Join-Path $PSScriptRoot '../../.tools/sheenbidi'
    if (!(Test-Path "$scintilla/src") -or !(Test-Path "$bidi/Source")) { throw 'Run scripts\mise run bootstrap first.' }
    $source = Join-Path $Cache 'windows-scintilla-patched'
    if (Test-Path $source) { Remove-Item $source -Recurse -Force }
    Copy-Item $scintilla $source -Recurse
    # The store is read-only on Unix; keep the copy writable for the patch either way.
    Get-ChildItem $source -Recurse -File | ForEach-Object { $_.IsReadOnly = $false }
    Push-Location $source
    try { Invoke-Native git @('apply', '--no-index', '--unsafe-paths', "$PSScriptRoot/Native/EditView.patch") }
    finally { Pop-Location }
    $flags = @('-target', $target, "-mcpu=$ZigWindowsCpu", '-O2', '-DNDEBUG', '-fvisibility=hidden')
    $objects = @((Join-Path $out 'SheenBidi.o'))
    Invoke-Zig $zig $RuntimeIdentifier $out (@('cc', '-std=c99') + $flags + @('-DSB_CONFIG_UNITY', '-I', "$bidi/Headers", '-I', "$bidi/Source", '-c', "$bidi/Source/SheenBidi.c", '-o', $objects[0]))
    $files = @(Get-ChildItem "$source/src/*.cxx" | Where-Object { $_.Name -notin @('AutoComplete.cxx', 'CallTip.cxx', 'ScintillaBase.cxx') }) + @(Get-ChildItem "$PSScriptRoot/Native/*.cxx")
    foreach ($file in $files) {
        $name = $file.BaseName
        if ($file.DirectoryName -eq (Join-Path $PSScriptRoot 'Native')) { $name = "Bridge$name" }
        $object = Join-Path $out "$name.o"
        Invoke-Zig $zig $RuntimeIdentifier $out (@('c++', '-std=c++17') + $flags + @('-I', "$source/include", '-I', "$source/src", '-I', "$bidi/Headers", '-c', $file.FullName, '-o', $object))
        $objects += $object
    }
    Invoke-Zig $zig $RuntimeIdentifier $out (@('c++', '-target', $target, "-mcpu=$ZigWindowsCpu", '-shared', '-static-libstdc++') + $objects + @('-o', $library))
    Set-Content $stamp $fingerprint
}
finally { $buildLock.Dispose() }
