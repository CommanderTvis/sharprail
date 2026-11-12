# Windows counterpart of the on-*.sh hook scripts, which need bash, jq and curl. Reads the hook's JSON from
# stdin and reports the same status payload (build-payload.sh) to SHARPRAIL_AGENT_STATUS_URL; the on-*.bat
# files call it with the event name. Silent outside a SharpRail terminal, and never fails the agent.
param([Parameter(Mandatory)][ValidateSet('session_start', 'prompt_submit', 'tool_complete', 'model_switch', 'permission_request', 'stop', 'stop_failure')][string] $Event)
$ErrorActionPreference = 'SilentlyContinue'
$raw = [Console]::In.ReadToEnd()
$in = if ($raw) { $raw | ConvertFrom-Json } else { [pscustomobject]@{} }

function Shorten([string] $text, [int] $limit) { if ($text -and $text.Length -gt $limit) { $text.Substring(0, $limit - 3) + '...' } else { $text } }

function SessionModel([string] $transcript) {
    if (!$transcript -or !(Test-Path $transcript)) { return '' }
    $turn = Get-Content $transcript -Tail 400 -Encoding UTF8 | ForEach-Object { try { $_ | ConvertFrom-Json } catch { } } |
        Where-Object { $_.type -eq 'assistant' -and $_.message.model -and $_.message.model -ne '<synthetic>' } | Select-Object -Last 1
    if ($turn) { $turn.message.model } else { '' }
}

function Payload([string] $name, [hashtable] $extra = @{}) {
    $model = [string]$in.model
    if (!$model) { $model = SessionModel ([string]$in.transcript_path) }
    $body = [ordered]@{ v = 1; agent = 'claude'; event = $name; session_id = [string]$in.session_id; cwd = [string]$in.cwd
        project = if ($in.cwd) { Split-Path ([string]$in.cwd) -Leaf } else { '' } }
    if ($in.transcript_path) { $body.transcript_path = [string]$in.transcript_path }
    foreach ($key in $extra.Keys) { $body[$key] = $extra[$key] }
    if (!$body.Contains('model') -and $model) { $body.model = $model }
    if (!$body.Contains('effort') -and $in.effort.level) { $body.effort = [string]$in.effort.level }
    $body
}

function Report($body) {
    if (!$body -or !$env:SHARPRAIL_AGENT_STATUS_URL) { return }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($body | ConvertTo-Json -Compress -Depth 8))
    try { Invoke-RestMethod -Uri $env:SHARPRAIL_AGENT_STATUS_URL -Method Post -Body $bytes -ContentType 'application/json' -TimeoutSec 2 | Out-Null } catch { }
}

function TextOf($content) {
    if ($content -is [string]) { return $content }
    (@($content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join ' ')
}

switch ($Event) {
    'session_start' {
        $manifest = Join-Path $PSScriptRoot '../.claude-plugin/plugin.json'
        $version = if (Test-Path $manifest) { (Get-Content $manifest -Raw | ConvertFrom-Json).version } else { $null }
        Report (Payload 'session_start' @{ plugin_version = if ($version) { $version } else { 'unknown' } })
    }
    'prompt_submit' { Report (Payload 'prompt_submit' @{ query = Shorten ([string]$in.prompt) 200 }) }
    'tool_complete' {
        $extra = @{ tool_name = [string]$in.tool_name }
        if ($in.tool_name -eq 'TodoWrite') {
            $extra.todos = @($in.tool_input.todos | ForEach-Object {
                $todo = [ordered]@{ content = $_.content; status = $_.status }
                if ($_.activeForm) { $todo.activeForm = $_.activeForm }
                $todo })
        }
        Report (Payload 'tool_complete' $extra)
    }
    'model_switch' { if ($in.to_model) { Report (Payload 'model_switch' @{ model = [string]$in.to_model }) } }
    'permission_request' {
        $tool = if ($in.tool_name) { [string]$in.tool_name } else { 'unknown' }
        $preview = if ($in.tool_input.command) { [string]$in.tool_input.command } elseif ($in.tool_input.file_path) { [string]$in.tool_input.file_path }
            elseif ($in.tool_input) { Shorten ($in.tool_input | ConvertTo-Json -Compress) 80 } else { '' }
        $summary = "Wants to run $tool"
        if ($preview) { $summary += ': ' + (Shorten $preview 120) }
        Report (Payload 'permission_request' @{ summary = $summary; tool_name = $tool })
    }
    'stop_failure' { Report (Payload 'stop_failure' @{ response = [string]$in.last_assistant_message; error_type = [string]$in.error }) }
    'stop' {
        # A continuation's Stop still ends a turn; only the desktop notification is suppressed.
        if ($in.stop_hook_active -eq $true) { Report (Payload 'stop' @{ notify = $false }); return }
        Start-Sleep -Milliseconds 300
        $query = ''; $response = ''
        $path = [string]$in.transcript_path
        if ($path -and (Test-Path $path)) {
            $turns = Get-Content $path -Encoding UTF8 | ForEach-Object { try { $_ | ConvertFrom-Json } catch { } }
            $user = $turns | Where-Object { $_.type -eq 'user' -and ($_.message.content -is [string] -or @($_.message.content | Where-Object { $_.type -eq 'text' }).Count -gt 0) } | Select-Object -Last 1
            $assistant = $turns | Where-Object { $_.type -eq 'assistant' -and $_.message.content } | Select-Object -Last 1
            if ($user) { $query = Shorten (TextOf $user.message.content) 200 }
            if ($assistant) { $response = Shorten (TextOf $assistant.message.content) 200 }
        }
        Report (Payload 'stop' @{ query = $query; response = $response })
    }
}
