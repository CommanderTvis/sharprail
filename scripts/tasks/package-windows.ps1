param([ValidateSet('win-x64', 'win-arm64')][string] $RuntimeIdentifier = 'win-x64')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../windows-native.ps1"
$root = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$dotnet = 'dotnet'
# win-x64 keeps its original locations; win-arm64 publishes beside them without replacing them.
$directory = if ($RuntimeIdentifier -eq 'win-x64') { 'windows' } else { 'windows-arm64' }
$output = Join-Path $root "artifacts/$directory"
$running = Get-Process -Name 'SharpRail.UI', 'SharpRail.Host.Remote', 'SharpRail.Checks' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw 'Close the running Windows package before replacing it.' }
if (Test-Path $output) { Remove-Item $output -Recurse -Force }
Push-Location $root
try {
    Invoke-Native $dotnet @('build', 'SharpRail.slnx', '-c', 'Release')
    # One locked restore (packages.lock.json lists every shipped RID) that also fetches the runtime, ILCompiler and Crossgen
    # packs; the publishes reuse it, since a `-r` restore would not match the lock's RuntimeIdentifiers.
    Invoke-Native $dotnet @('restore', 'SharpRail.slnx', '-p:SelfContained=true', '-p:PublishReadyToRun=true')
    # The About screen's version stamp, as the nightly names its builds: UTC date and short commit.
    $stamp = "$([DateTime]::UtcNow.ToString('yyyyMMdd'))-$(git rev-parse --short HEAD)"
    $publish = @('-c', 'Release', '-r', $RuntimeIdentifier, '--no-restore', "-p:SharpRailBuildStamp=$stamp")
    # The client and host are NativeAOT, linked by Visual Studio's C++ linker; the checks stay a JIT program (ReadyToRun).
    Invoke-Native $dotnet (@('publish', 'src/SharpRail.UI') + $publish + @('-o', (Join-Path $output 'client')))
    Invoke-Native $dotnet (@('publish', 'src/SharpRail.Host.Remote') + $publish + @('-o', (Join-Path $output 'host')))
    Invoke-Native $dotnet (@('publish', 'tests/SharpRail.Checks') + $publish + @('--self-contained', 'true', '-p:PublishReadyToRun=true', '-o', (Join-Path $output 'checks')))
    Copy-Item 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md', 'licenses', 'WINDOWS.md' $output -Recurse
    Compress-Archive -Path "$output/client", "$output/host", "$output/licenses", "$output/LICENSE", "$output/NOTICE", "$output/THIRD-PARTY-NOTICES.md", "$output/WINDOWS.md" -DestinationPath (Join-Path $root "artifacts/SharpRail-$RuntimeIdentifier.zip") -Force
}
finally { Pop-Location }
