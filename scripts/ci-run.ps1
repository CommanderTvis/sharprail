# Windows counterpart of ci-run.sh: runs a command in a GitHub Actions step and, when it fails, annotates the first
# error lines and writes the log's tail to the step summary. Usage: ci-run.ps1 "<step title>" <command> [arguments...]
param([Parameter(Mandatory, Position = 0)][string]$Title, [Parameter(Position = 1, ValueFromRemainingArguments)][string[]]$Command)
$ErrorActionPreference = 'Continue'
if ($env:GITHUB_ACTIONS -ne 'true') { & $Command[0] @($Command | Select-Object -Skip 1); exit $LASTEXITCODE }
$dir = Join-Path ($(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP })) 'sharprail-ci'
New-Item -ItemType Directory -Force $dir | Out-Null
$log = Join-Path $dir (($Title -replace '[^A-Za-z0-9]', '-') + "-$PID.log")
& $Command[0] @($Command | Select-Object -Skip 1) 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $log
$code = $LASTEXITCODE
if ($code -ne 0) {
    $escape = { param($s) $s.Replace('%', '%25').Replace("`r", '').Replace("`n", '%0A') }
    $lines = @(Get-Content $log -ErrorAction SilentlyContinue)
    $errors = @($lines | Where-Object { $_ -match ': error [A-Za-z]*\d+:|(^|[^A-Za-z])(error|ERROR|fatal|FAIL|FAILED)[: ]|NETSDK\d+|MSB\d{4}|Unhandled exception' } |
        Where-Object { $_ -notmatch '(^|[^0-9])0 Error\(s\)|Warning\(s\)|Time Elapsed|^::' } | Select-Object -Unique -First 12)
    foreach ($line in $errors) {
        if ($line -match '^\s*(?<file>.+)\((?<row>\d+),(?<col>\d+)\): error (?<msg>.*)$') {
            "::error file=$($Matches.file),line=$($Matches.row),col=$($Matches.col),title=$Title::$(& $escape $Matches.msg)"
        } else { "::error title=$Title::$(& $escape $line)" }
    }
    "::error title=$Title failed::Step `"$Title`" exited with code $code; see the run summary for the first errors and the log tail."
    if ($env:GITHUB_STEP_SUMMARY) {
        $summary = @("### Step failed: $Title (exit code $code)", '')
        if ($errors.Count -gt 0) { $summary += @('First error lines:', '', '```') + $errors + @('```', '') }
        else { $summary += @('No error line matched; the log tail below is the best lead.', '') }
        $summary += @('<details><summary>Last 40 lines of the log</summary>', '', '```') + ($lines | Select-Object -Last 40 | ForEach-Object { $_.Replace('```', "'''") }) + @('```', '</details>', '')
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $summary
    }
}
exit $code
