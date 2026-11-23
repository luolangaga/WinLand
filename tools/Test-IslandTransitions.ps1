param([Parameter(Mandatory)][int]$AppPid,
    [string]$ArtifactDirectory = (Join-Path $PSScriptRoot '../artifacts/island-transitions'))
$ErrorActionPreference = 'Stop'
$workflow = [guid]::NewGuid().ToString('N')
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = $workflow
function UI([string[]]$Arguments) {
    $output = @(& winapp ui @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "winapp ui failed: $($output -join ' ')" }
    $output
}
try {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$AppPid"
    if ($process.CommandLine -notmatch '--motion-preview') { throw 'Use only an isolated motion preview.' }
    $windows = @(UI @('list-windows','-a',"$AppPid",'--json') | ConvertFrom-Json)
    $controls = [string]($windows | Where-Object title -eq 'WinIsland Motion Preview').hwnd
    $island = [string]($windows | Where-Object title -eq 'WinIsland').hwnd
    if (!$controls -or !$island) { throw 'Preview windows are missing.' }
    $ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
    New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
    UI @('invoke','PreviewEdgeLight','-w',$controls) | Out-Null
    Start-Sleep -Milliseconds 400
    $output = Join-Path $ArtifactDirectory ('transition-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.mp4')
    $recorder = Start-Process -FilePath (Get-Command winapp).Source -WindowStyle Hidden -PassThru `
        -ArgumentList @('ui','record','-w',$island,'--duration-sec','7','--fps','30','--frames','--output',('"'+$output+'"'),'--json') `
        -RedirectStandardOutput ($output+'.log') -RedirectStandardError ($output+'.err')
    Start-Sleep -Milliseconds 1200
    foreach ($pause in @(800,650,800,110,800,650)) {
        UI @('invoke','PreviewEdgeExpansion','-w',$controls) | Out-Null
        Start-Sleep -Milliseconds $pause
    }
    if (!$recorder.WaitForExit(20000)) { throw 'Recording timed out.' }
    if ($recorder.ExitCode -ne 0) { throw "Recording failed: $(Get-Content ($output+'.err') -Raw)" }
    if (!(Test-Path -LiteralPath $output)) { throw 'Recording was not delivered.' }
    $manifest = Get-Content ([IO.Path]::ChangeExtension($output,'frames')+'/manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.status -ne 'complete') { throw 'Frame capture did not complete.' }
    Write-Output "Recording: $output"
} finally {
    UI @('yield') | Out-Null
    $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow
}
