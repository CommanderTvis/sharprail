# Counterpart of link-tools.sh: link-tools.ps1 <mise> name=tool@version... makes .tools\<name> a junction to the
# shared install of each tool. Installs are not made read-only here; the store lives in the user's profile.
param([string] $Mise, [Parameter(ValueFromRemainingArguments)] [string[]] $Pairs)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/..")
$tools = Join-Path $root '.tools'
New-Item -ItemType Directory $tools -Force | Out-Null
foreach ($pair in $Pairs) {
    $name, $tool = $pair -split '=', 2
    $target = & $Mise where $tool 2>$null
    if ($LASTEXITCODE -ne 0 -or !$target -or !(Test-Path $target)) { continue }
    $target = (Resolve-Path $target).Path
    $link = Join-Path $tools $name
    $item = Get-Item $link -ErrorAction SilentlyContinue
    if ($item -and !$item.LinkType) {
        Rename-Item $link "$name.local-$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
        Write-Warning "Moved the checkout-local .tools/$name aside; it can be deleted."
    } elseif ($item -and $item.Target -ne $target) {
        $item.Delete()
        $item = $null
    }
    if (!(Get-Item $link -ErrorAction SilentlyContinue)) { New-Item -ItemType Junction -Path $link -Target $target | Out-Null }
}
