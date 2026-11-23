param([Parameter(Mandatory)][int]$AppPid,[string]$ArtifactDirectory=(Join-Path $PSScriptRoot '..\artifacts\motion-preview\artwork-wave'))
$ErrorActionPreference='Stop'
$env:WINAPP_UI_WORKFLOW_ID=[guid]::NewGuid().ToString('N')
$dir=[IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$results=[Collections.Generic.List[object]]::new()
function UI([string[]]$Arguments,[string]$Window) {
 $output=@(& winapp ui @Arguments -w $Window)
 if ($LASTEXITCODE -ne 0) {throw "$($Arguments -join ' '): $($output -join "`n")"}
 $output
}
function Shot([string]$Name,[string]$Window) { UI @('screenshot','--output',(Join-Path $dir ($Name+'.png'))) $Window | Out-Null }
function Check([string]$Name,[scriptblock]$Action) {
 try {& $Action | Out-Null; $results.Add(@{name=$Name;status='PASS'})}
 catch {$results.Add(@{name=$Name;status='FAIL';detail="$_"}); throw}
}
try {
 $windows=@(& winapp ui list-windows -a $AppPid --json | ConvertFrom-Json)
 $controls=[string]($windows | Where-Object title -eq 'WinIsland Motion Preview').hwnd
 $island=[string]($windows | Where-Object title -eq 'WinIsland').hwnd
 if (!$controls -or !$island) {throw 'Isolated preview required'}
 Check 'Classic wave independent of background' {
  UI @('invoke','PreviewWaveWithoutGlow') $controls
  Start-Sleep -Milliseconds 300; Shot 'wave-a' $island
  Start-Sleep -Milliseconds 137; Shot 'wave-b' $island
 }
 Check 'Pause and disable stop wave' {
  UI @('invoke','PreviewWavePause') $controls; Shot 'wave-paused' $island
  UI @('invoke','PreviewWaveWithoutGlow') $controls
  UI @('invoke','PreviewWaveDisable') $controls; Shot 'wave-disabled' $island
 }
 Check 'Compact artwork flip and arrival' {
  UI @('invoke','PreviewCoverSwitch') $controls; Shot 'compact-flip' $island
  Start-Sleep -Milliseconds 1800; Shot 'compact-arrival' $island
 }
 Check 'Rapid artwork updates retain last source' {
  UI @('invoke','PreviewCoverRapid') $controls
  Start-Sleep -Milliseconds 2100; Shot 'compact-rapid' $island
  Get-Process -Id $AppPid -ErrorAction Stop
 }
 Check 'Spotlight artwork flip and arrival' {
  UI @('invoke','PreviewMedia') $controls; Start-Sleep -Milliseconds 700
  $spotlight=[string](@(& winapp ui list-windows -a $AppPid --json | ConvertFrom-Json) | Where-Object title -eq 'WinIsland Spotlight').hwnd
  Shot 'spotlight-before' $spotlight
  UI @('invoke','PreviewCoverSwitch') $controls; Shot 'spotlight-flip' $spotlight
  Start-Sleep -Milliseconds 1800; Shot 'spotlight-arrival' $spotlight
 }
 Check 'Closing during flip restores latest compact artwork' {
  UI @('invoke','PreviewCoverSwitch') $controls
  UI @('invoke','PreviewClose') $controls
  Start-Sleep -Milliseconds 1900; Shot 'compact-restored' $island
  Get-Process -Id $AppPid -ErrorAction Stop
 }
} catch {
 if (!@($results | Where-Object status -eq 'FAIL').Count) {$results.Add(@{name='Setup';status='FAIL';detail="$_"})}
} finally { & winapp ui yield | Out-Null; Remove-Item Env:WINAPP_UI_WORKFLOW_ID -ErrorAction SilentlyContinue }
$failed=@($results | Where-Object status -eq 'FAIL').Count
@{target='local';appPid=$AppPid;passed=$results.Count-$failed;failed=$failed;results=$results.ToArray()} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $dir 'results.json') -Encoding utf8
Write-Host "Passed: $($results.Count-$failed) | Failed: $failed"
if ($failed) {exit 1}
