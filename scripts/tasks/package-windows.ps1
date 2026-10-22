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
    # One locked restore (packages.lock.json lists every shipped RID) that also fetches the runtime and Crossgen packs;
    # the publishes reuse it, since a `-r` restore would not match the lock's RuntimeIdentifiers.
    Invoke-Native $dotnet @('restore', 'SharpRail.slnx', '-p:SelfContained=true', '-p:PublishReadyToRun=true')
    # The About screen's version stamp, as the nightly names its builds: UTC date and short commit.
    $stamp = "$([DateTime]::UtcNow.ToString('yyyyMMdd'))-$(git rev-parse --short HEAD)"
    foreach ($target in @(@('src/SharpRail.UI', 'client'), @('src/SharpRail.Host.Remote', 'host'), @('tests/SharpRail.Checks', 'checks'))) {
        Invoke-Native $dotnet @('publish', $target[0], '-c', 'Release', '-r', $RuntimeIdentifier, '--self-contained', 'true', '--no-restore',
            '-p:PublishReadyToRun=true', '-p:PublishReadyToRunComposite=true', "-p:SharpRailBuildStamp=$stamp", '-o', (Join-Path $output $target[1]))
    }
    Copy-Item 'tests/SharpRail.Checks/bin/Release/net10.0/plugin-fixture' (Join-Path $output 'checks/plugin-fixture') -Recurse
    Copy-Item 'THIRD-PARTY-NOTICES.md', 'licenses', 'WINDOWS.md' $output -Recurse
    Compress-Archive -Path "$output/client", "$output/host", "$output/licenses", "$output/THIRD-PARTY-NOTICES.md", "$output/WINDOWS.md" -DestinationPath (Join-Path $root "artifacts/SharpRail-$RuntimeIdentifier.zip") -Force
}
finally { Pop-Location }
