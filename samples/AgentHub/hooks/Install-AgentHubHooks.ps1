param(
    [ValidateSet('Claude Code','Codex','CodeBuddy','WorkBuddy','Qwen Code','ZCode','Qoder','Qoder CN','QoderWork')]
    [string]$Agent = 'Claude Code',
    [string]$ConfigPath,
    [switch]$Remove,
    [switch]$Preview
)
$ErrorActionPreference = 'Stop'
$homes = @{
    'Claude Code'='.claude'; 'Codex'='.codex'; 'CodeBuddy'='.codebuddy'; 'WorkBuddy'='.workbuddy';
    'Qwen Code'='.qwen'; 'ZCode'='.zcode\cli'; 'Qoder'='.qoder'; 'Qoder CN'='.qoder-cn'; 'QoderWork'='.qoderwork'
}
if (-not $ConfigPath) {
    $clientHome = Join-Path ([Environment]::GetFolderPath('UserProfile')) $homes[$Agent]
    if ($Agent -eq 'Codex' -and $env:CODEX_HOME) { $clientHome = $env:CODEX_HOME }
    if ($Agent -eq 'Claude Code' -and $env:CLAUDE_CONFIG_DIR) { $clientHome = $env:CLAUDE_CONFIG_DIR }
    if ($Agent -eq 'Qwen Code' -and $env:QWEN_HOME) { $clientHome = $env:QWEN_HOME }
    if ($Agent -eq 'CodeBuddy' -and $env:CODEBUDDY_CONFIG_DIR) { $clientHome = $env:CODEBUDDY_CONFIG_DIR }
    $file = if ($Agent -eq 'Codex') { 'hooks.json' } elseif ($Agent -eq 'ZCode') { 'config.json' } else { 'settings.json' }
    $ConfigPath = Join-Path $clientHome $file
}
$script = Join-Path $PSScriptRoot 'agenthub-hook.ps1'
$command = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $script + '" -Agent "' + $Agent + '"'
$config = if (Test-Path -LiteralPath $ConfigPath) { Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
if (-not $config.PSObject.Properties['hooks']) { $config | Add-Member hooks ([pscustomobject]@{}) }
if ($Agent -eq 'ZCode' -and -not $config.hooks.PSObject.Properties['events']) { $config.hooks | Add-Member events ([pscustomobject]@{}) }
$eventConfig = if ($Agent -eq 'ZCode') { $config.hooks.events } else { $config.hooks }
# Only these exact AgentHub commands are owned; other hooks and all unrelated fields survive.
$events = if ($Agent -eq 'Claude Code') { @('PermissionRequest','Stop','Notification','UserPromptSubmit','SessionEnd') }
    elseif ($Agent -in @('CodeBuddy','WorkBuddy','Qwen Code')) { @('PermissionRequest','Stop') }
    else { @('PermissionRequest') }
foreach ($event in $events) {
    $kept = @()
    if ($eventConfig.PSObject.Properties[$event]) {
        foreach ($entry in @($eventConfig.$event)) {
            $entryHooks = @($entry.hooks | Where-Object { $_.command -ne $command })
            if ($entryHooks.Count) { $entry.hooks = $entryHooks; $kept += $entry }
        }
    }
    if (-not $Remove) {
        $timeout = if ($event -eq 'PermissionRequest') { 610 } else { 5 }
        if ($Agent -eq 'Qwen Code') { $timeout *= 1000 }
        $group = [pscustomobject]@{ hooks=@([pscustomobject]@{ type='command'; command=$command; timeout=$timeout }) }
        if ($Agent -ne 'ZCode') { $group | Add-Member matcher '' }
        $kept += $group
    }
    $eventConfig | Add-Member -NotePropertyName $event -NotePropertyValue @($kept) -Force
}
$json = $config | ConvertTo-Json -Depth 100
if ($Preview) { $json; return }
$parent = Split-Path -Parent $ConfigPath
[IO.Directory]::CreateDirectory($parent) | Out-Null
if (Test-Path -LiteralPath $ConfigPath) { Copy-Item -LiteralPath $ConfigPath -Destination ($ConfigPath + '.agenthub-backup-' + (Get-Date -Format 'yyyyMMddHHmmssfff')) }
# Same-directory atomic replacement, rather than truncating a live config.
$tempPath = $ConfigPath + '.agenthub-' + [guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($tempPath, $json, (New-Object Text.UTF8Encoding($false)))
Move-Item -LiteralPath $tempPath -Destination $ConfigPath -Force
Write-Output "AgentHub hooks updated: $ConfigPath. Codex users must review/trust the hook themselves in /hooks or Desktop settings."
