param([switch]$Ui)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    & dotnet run --project tests/WinIsland.Regression
    if ($LASTEXITCODE -ne 0) { throw 'Core regression scenarios failed.' }
    & dotnet build WinIsland/WinIsland.csproj -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }
    if ($Ui) {
        if (Get-Process WinIsland -ErrorAction SilentlyContinue) {
            throw 'WinIsland is already running. Close it before starting the isolated UI regression.'
        }
        $output = & dotnet msbuild WinIsland/WinIsland.csproj -getProperty:TargetDir
        if ($LASTEXITCODE -ne 0) { throw 'Could not locate build output.' }
        $appDirectory = $output.Trim()
        $report = Join-Path $appDirectory 'reliability-tests/results.json'
        if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
        $process = Start-Process -FilePath (Join-Path $appDirectory 'WinIsland.exe') -ArgumentList '--reliability-tests' -WindowStyle Hidden -PassThru
        if (!$process.WaitForExit(60000)) {
            Stop-Process -Id $process.Id
            throw 'Isolated UI regression timed out.'
        }
        $process.Refresh();
        if (!(Test-Path -LiteralPath $report)) { throw 'UI regression did not produce a report.' }
        $results = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        $results | Format-Table name,status,detail -Wrap
        if ($process.ExitCode -ne 0 -or @($results | Where-Object status -ne 'PASS').Count -gt 0) {
            throw 'UI regression scenarios failed.'
        }
    }
}
finally { Pop-Location }
