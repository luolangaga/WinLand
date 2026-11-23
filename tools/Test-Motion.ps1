param(
    [Parameter(Mandatory)][int]$AppPid,
    [switch]$IncludeSamples,
    [string]$ArtifactDirectory = (Join-Path $PSScriptRoot '..\artifacts\motion-preview\ui'),
    [string]$WorkflowId = [guid]::NewGuid().ToString('N')
)

# Run against the opt-in --motion-preview process, never the normal user's instance.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = $WorkflowId
$results = [Collections.Generic.List[object]]::new()
$shots = [Collections.Generic.List[string]]::new()
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null

function UI([string[]]$Arguments, [string]$Window) {
    $selector = if ($Window) { @('-w', $Window) } else { @('-a', "$AppPid") }
    $output = @(& winapp ui @Arguments @selector)
    if ($LASTEXITCODE -ne 0) { throw "winapp ui $($Arguments -join ' ') failed: $($output -join "`n")" }
    $output
}
function Check([string]$Name, [scriptblock]$Test) {
    try { & $Test | Out-Null; $results.Add(@{name=$Name; status='PASS'}) }
    catch { $results.Add(@{name=$Name; status='FAIL'; detail="$_"}); throw }
}
function Capture([string]$Name, [string]$Window) {
    $path = Join-Path $ArtifactDirectory ($Name + '.png')
    UI @('screenshot', '--output', $path) $Window | Out-Null
    if (!(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Missing screenshot: $path" }
    $shots.Add($path)
}

try {
    $windows = @(UI @('list-windows', '--json') | ConvertFrom-Json)
    $controls = [string]($windows | Where-Object title -eq 'WinIsland Motion Preview' | Select-Object -First 1).hwnd
    if (!$controls) { throw 'The PID must belong to an active --motion-preview instance.' }
    UI @('invoke','PreviewSettingsClose') $controls | Out-Null
    UI @('invoke','PreviewClose') $controls | Out-Null
    Start-Sleep -Milliseconds 700
    $windows = @(UI @('list-windows', '--json') | ConvertFrom-Json)
    $island = [string]($windows | Where-Object title -eq 'WinIsland' | Select-Object -First 1).hwnd
    if (!$island) { throw 'The compact island did not return after closing the overlay.' }
    Check 'Preview controls' { UI @('wait-for','PreviewReverse'); Capture '01-controls' $controls }
    Check 'Media entrance' {
        UI @('invoke','PreviewMedia') $controls
        $script:spotlight = [string](@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object title -eq 'WinIsland Spotlight' | Select-Object -First 1).hwnd
        UI @('wait-for','SpotlightPlayPause_Click','-t','3000') $spotlight
        Start-Sleep -Milliseconds 600
        Capture '02-media' $spotlight
    }
    Check 'Progress fill and knob at both endpoints' {
        UI @('invoke','PreviewProgress25') $controls
        Start-Sleep -Milliseconds 400
        Capture 'progress-25' $spotlight
        UI @('invoke','PreviewProgress75') $controls
        Start-Sleep -Milliseconds 400
        Capture 'progress-75' $spotlight
    }
    Check 'Battery replacement'  {
        UI @('invoke','PreviewBattery') $controls
        UI @('wait-for','ValueRemainingText','-t','3000') $spotlight
        Start-Sleep -Milliseconds 600
        Capture '03-battery' $spotlight
    }
    Check 'Interrupt entrance, close and replace' {
        UI @('invoke','PreviewReverse') $controls
        UI @('wait-for','SpotlightPlayPause_Click','-t','3000') $spotlight
        Start-Sleep -Milliseconds 800
        UI @('wait-for','ValuePowerText','--gone','-t','2000') $spotlight
        Capture '04-reversed-media' $spotlight
    }
    Check 'Close returns to compact island' {
        UI @('invoke','PreviewClose') $controls
        Start-Sleep -Milliseconds 600
        UI @('wait-for','Quiet mornings','-t','2000') $island
        UI @('wait-for','PreviewStatus','--contains','--value','Closed') $controls
        Capture '05-returned-island' $island
    }
    Check 'Two-line message and restoration' {
        UI @('invoke','PreviewMessage') $controls
        UI @('wait-for','内容交接测试','-t','2000') $island
        Capture '06-message' $island
        UI @('wait-for','内容交接测试','--gone','-t','6000') $island
        UI @('wait-for','Quiet mornings') $island
    }
    Check 'Appearance and bottom placement' {
        UI @('invoke','PreviewStyle') $controls
        UI @('invoke','PreviewBottom') $controls
        UI @('invoke','PreviewBattery') $controls
        UI @('wait-for','ValueRemainingText') $spotlight
        Start-Sleep -Milliseconds 600
        Capture '07-fluent-battery' $spotlight
        UI @('invoke','PreviewClose') $controls
        Start-Sleep -Milliseconds 600
        UI @('wait-for','Quiet mornings') $island
        Capture '08-bottom-island' $island
        UI @('invoke','PreviewBottom') $controls
        UI @('invoke','PreviewStyle') $controls
    }
    Check 'Hover expansion and legacy dual-view queue' {
        UI @('hover','Quiet mornings') $island
        UI @('wait-for','QueueNext','-t','3000') $island
        Start-Sleep -Milliseconds 500
        Capture '09-expanded-queue' $island
        UI @('invoke','QueueNext') $island
        UI @('wait-for','队列内容 3','-t','2000') $island
        Start-Sleep -Milliseconds 400
        Capture '10-paged-queue' $island
        UI @('hover','PreviewMedia') $controls
        Start-Sleep -Milliseconds 900
        UI @('wait-for','QueueNext','--gone','-t','2000') $island
    }
    Check 'Fluent settings layout' {
        UI @('invoke','PreviewSettings') $controls
        Start-Sleep -Milliseconds 350
        $settingsWindow = [string](@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object { $_.hwnd -ne $controls -and $_.hwnd -ne $spotlight -and $_.width -ge 1500 -and $_.className -eq 'WinUIDesktopWin32WindowClass' } | Select-Object -First 1).hwnd
        UI @('wait-for','style.fluent') $settingsWindow
        Capture '11-fluent-settings' $settingsWindow
        UI @('invoke','PreviewSettingsClose') $controls
    }
    Check 'Process survives all transitions'  { Get-Process -Id $AppPid -ErrorAction Stop }
    if ($IncludeSamples) {
        foreach ($sample in @(@('HardwareMonitor','CPU'), @('WeatherIsland','上海'), @('DeviceIsland','便携工作盘'), @('ClipboardIsland','设计评审'))) {
            Check ($sample[0] + ' layout') {
                UI @('invoke',('Preview' + $sample[0])) $controls
                Start-Sleep -Milliseconds 350
                $matches = UI @('search',$sample[1],'--json') $spotlight | ConvertFrom-Json
                if (!$matches.matchCount -or !@($matches.matches | Where-Object { !$_.isOffscreen }).Count) { throw ('Sample text not visible: '+$sample[1]) }
                Start-Sleep -Milliseconds 700
                Capture ('sample-' + $sample[0]) $spotlight
            }
        }
        UI @('invoke','PreviewMedia') $controls
        UI @('invoke','PreviewClose') $controls
    }
} catch {
    if (!($results | Where-Object status -eq 'FAIL')) { $results.Add(@{name='Test setup';status='FAIL';detail="$_"}) }
} finally {
    try { & winapp ui yield | Out-Null } finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow }
}
$failed = @($results | Where-Object status -eq 'FAIL').Count
@{target='local';appPid=$AppPid;passed=$results.Count-$failed;failed=$failed;results=$results.ToArray();screenshots=$shots.ToArray()} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
Write-Host "Passed: $($results.Count-$failed) | Failed: $failed"
if ($failed) { exit 1 }


