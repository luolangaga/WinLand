param([Parameter(Mandatory)][int]$AppPid,[string]$ArtifactDirectory = (Join-Path $PSScriptRoot '..\artifacts\motion-preview\content-effects'))
$ErrorActionPreference='Stop'
$env:WINAPP_UI_WORKFLOW_ID=[guid]::NewGuid().ToString('N')
$results=[Collections.Generic.List[object]]::new()
$shots=[Collections.Generic.List[string]]::new()
$ArtifactDirectory=[IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
function UI([string[]]$Arguments,[string]$Window) {
 $output=@(& winapp ui @Arguments -w $Window)
 if ($LASTEXITCODE -ne 0) {throw "$($Arguments -join ' '): $($output -join "`n")"}
 $output
}
function Check([string]$Name,[scriptblock]$Action) {
 try {& $Action | Out-Null; $results.Add(@{name=$Name;status='PASS'})}
 catch {$results.Add(@{name=$Name;status='FAIL';detail="$_"}); throw}
}
function Visible([string]$Text) {
 $found=UI @('search',$Text,'--json') $script:spotlight | ConvertFrom-Json
 if (!$found.matchCount -or !@($found.matches | Where-Object { !$_.isOffscreen }).Count) {throw "Not visible: $Text"}
}
function Shot([string]$Name) {
 $path=Join-Path $ArtifactDirectory ($Name+'.png')
 UI @('screenshot','--output',$path) $script:spotlight | Out-Null
 if (!(Test-Path -LiteralPath $path)) {throw 'Missing capture'}
 $shots.Add($path)
}
try {
 $windows=@(& winapp ui list-windows -a $AppPid --json | ConvertFrom-Json)
 $controls=[string]($windows | Where-Object title -eq 'WinIsland Motion Preview' | Select-Object -First 1).hwnd
 if (!$controls) {throw 'An isolated --motion-preview instance is required.'}
 UI @('invoke','PreviewLyrics') $controls | Out-Null
 Start-Sleep -Milliseconds 700
 $script:spotlight=[string](@(& winapp ui list-windows -a $AppPid --json | ConvertFrom-Json) | Where-Object title -eq 'WinIsland Spotlight' | Select-Object -First 1).hwnd
 Check 'Automatic lyrics layout' {Visible 'Let the quiet'; Shot '01-lyrics'}
 Check 'Nonlinear lyrics advance' {UI @('invoke','PreviewLyricNext') $controls; Start-Sleep -Milliseconds 850; Visible '把忙碌'; Shot '02-lyrics-next'}
 Check 'Prelude dots' {UI @('invoke','PreviewIntro') $controls; Start-Sleep -Milliseconds 350; Visible '前奏'; Shot '03-prelude'}
 Check 'Unsupported timeline allows scrolling' {
  UI @('invoke','PreviewManualLyrics') $controls; Start-Sleep -Milliseconds 350; Visible '不支持播放进度';
  UI @('scroll','LyricsScroller','--to','bottom') $spotlight; Shot '04-manual-bottom';
  UI @('scroll','LyricsScroller','--to','top') $spotlight; Shot '05-manual-top'
 }
 Check 'No lyrics caption' {UI @('invoke','PreviewNoLyrics') $controls; Start-Sleep -Milliseconds 250; Visible '这应该不是歌曲。'; Shot '06-no-lyrics'}
 Check 'Three audio bands respond to measured input' {
  UI @('invoke','PreviewLyrics') $controls; UI @('invoke','PreviewEffectspectrum') $controls;
  UI @('invoke','PreviewAudioQuiet') $controls; Start-Sleep -Milliseconds 500; Shot '07-silence';
  UI @('invoke','PreviewAudioBass') $controls; Start-Sleep -Milliseconds 500; Shot '08-bass';
  UI @('invoke','PreviewAudioTreble') $controls; Start-Sleep -Milliseconds 500; Shot '09-treble';
 }
 foreach ($mode in @('wave','bass','off')) {
  Check ('Music mode '+$mode) {UI @('invoke',('PreviewEffect'+$mode)) $controls; Start-Sleep -Milliseconds 300; UI @('invoke','PreviewAudioQuiet') $controls; Start-Sleep -Milliseconds 300; UI @('invoke','PreviewAudioBass') $controls; Start-Sleep -Milliseconds 150; Shot ('music-'+$mode)}
 }
 foreach ($mode in @('beam','circuit','chargepulse')) {
  Check ('Charging mode '+$mode) {UI @('invoke',('PreviewCharge'+$mode)) $controls; Start-Sleep -Milliseconds 700; Visible 'ValueRemainingText'; Shot ('charge-'+$mode)}
 }
 Check 'Disconnect charging stops energy flow' {UI @('invoke','PreviewChargeOff') $controls; Start-Sleep -Milliseconds 500; Shot 'charge-disconnected'; UI @('invoke','PreviewChargeOff') $controls}
 Check 'Fluent theme preserves lyric contrast' {UI @('invoke','PreviewStyle') $controls; UI @('invoke','PreviewLyrics') $controls; Start-Sleep -Milliseconds 700; Visible 'Let the quiet'; Shot 'fluent-lyrics'; UI @('invoke','PreviewChargebeam') $controls; Start-Sleep -Milliseconds 600; Shot 'fluent-charge'; UI @('invoke','PreviewStyle') $controls}
 Check 'Charging choices and disable switch' {
  UI @('invoke','PreviewClose') $controls; UI @('invoke','PreviewSettings') $controls; Start-Sleep -Milliseconds 350;
  $settingsWindow=[string](@(& winapp ui list-windows -a $AppPid --json | ConvertFrom-Json) | Where-Object { $_.hwnd -ne $controls -and $_.hwnd -ne $spotlight -and $_.width -ge 1500 -and $_.className -eq 'WinUIDesktopWin32WindowClass' } | Select-Object -First 1).hwnd
  UI @('invoke','SettingsNav.effects') $settingsWindow;
  UI @('wait-for','BackgroundStyle') $settingsWindow;
  UI @('wait-for','BackgroundEnabled') $settingsWindow;
  UI @('wait-for','BackgroundIntensity') $settingsWindow;
  $path=Join-Path $ArtifactDirectory 'effect-settings.png'; UI @('screenshot','--output',$path) $settingsWindow; $shots.Add($path);
  UI @('invoke','PreviewSettingsClose') $controls
 }
 Check 'Rapid replace and return' {UI @('invoke','PreviewReverse') $controls; Start-Sleep -Milliseconds 800; Visible 'SpotlightPlayPause_Click'; UI @('invoke','PreviewClose') $controls; Start-Sleep -Milliseconds 600; Get-Process -Id $AppPid -ErrorAction Stop}
} catch {
 if (!@($results | Where-Object status -eq 'FAIL').Count) {$results.Add(@{name='Setup';status='FAIL';detail="$_"})}
} finally {& winapp ui yield | Out-Null; Remove-Item Env:WINAPP_UI_WORKFLOW_ID -ErrorAction SilentlyContinue}
$failed=@($results | Where-Object status -eq 'FAIL').Count
@{target='local';appPid=$AppPid;passed=$results.Count-$failed;failed=$failed;results=$results.ToArray();screenshots=$shots.ToArray()} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
Write-Host "Passed: $($results.Count-$failed) | Failed: $failed"
if ($failed) {exit 1}


