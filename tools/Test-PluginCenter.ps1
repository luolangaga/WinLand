param([Parameter(Mandatory)][int]$AppPid, [Parameter(Mandatory)][string]$WindowHandle)
$ErrorActionPreference = 'Stop'
$env:WINAPP_UI_WORKFLOW_ID = 'winland-plugin-center'
$evidence = Join-Path $PSScriptRoot '../artifacts/plugin-center'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$results = [Collections.Generic.List[object]]::new()
function Ui([string[]]$Command) {
    $output = & winapp ui @Command -w $WindowHandle 2>&1
    if ($LASTEXITCODE -ne 0) { throw "winapp ui $($Command -join ' '): $($output -join ' ')" }
    $output
}
function Check([string]$Name, [scriptblock]$Action) {
    try { & $Action | Out-Null; $results.Add([pscustomobject]@{ name=$Name; status='PASS'; detail='' }) }
    catch { $results.Add([pscustomobject]@{ name=$Name; status='FAIL'; detail="$_" }) }
}
try {
    Check 'Unified discovery shows catalog and installed state' {
        Ui @('wait-for','PluginCenter.Results','--value','8 个插件','--contains','-t','5000')
        Ui @('wait-for','PluginCenter.Summary','--value','5 个已安装','--contains')
        Ui @('screenshot','--output',"$evidence/discover.png")
    }
    Check 'Name search and shared installed filter' {
        Ui @('set-value','TextBox','天气')
        Ui @('wait-for','PluginCenter.Results','--value','1 个插件','--contains')
        Ui @('invoke','PluginCenter.Installed','--action','select')
        Ui @('wait-for','PluginCenter.Results','--value','1 个插件','--contains')
        Ui @('set-value','TextBox','')
        Ui @('wait-for','PluginCenter.Results','--value','5 个插件','--contains')
    }
    Check 'Empty search state' {
        Ui @('set-value','TextBox','no-such-plugin-xxxx')
        Ui @('wait-for','PluginCenter.Results','--value','0 个插件','--contains')
        Ui @('wait-for','没有找到相关插件')
        Ui @('screenshot','--output',"$evidence/empty.png")
        Ui @('set-value','TextBox','')
    }
    Check 'Enable toggle updates the shared summary' {
        Ui @('invoke','Plugin.preview-0.Enabled','--action','toggle-off')
        Ui @('wait-for','PluginCenter.Summary','--value','2 个运行中','--contains')
        Ui @('invoke','Plugin.preview-0.Enabled','--action','toggle-on')
        Ui @('wait-for','PluginCenter.Summary','--value','3 个运行中','--contains')
    }
    Check 'Management exposes both independent priorities and logs' {
        Ui @('invoke','Plugin.preview-0.Details')
        Ui @('wait-for','plugin.preview-0.priority')
        Ui @('wait-for','plugin.preview-0.expandedPriority')
        Ui @('screenshot','--output',"$evidence/management.png")
        Ui @('invoke','Plugin.preview-0.Logs')
        Ui @('wait-for','PrimaryButton')
        Ui @('invoke','CloseButton')
    }
    Check 'Update filter and failed package installation recover' {
        Ui @('invoke','PluginCenter.Updates','--action','select')
        Ui @('wait-for','PluginCenter.Results','--value','1 个插件','--contains')
        Ui @('wait-for','Plugin.preview-4.Primary')
        Ui @('invoke','Plugin.preview-4.Primary')
        Ui @('wait-for','安装失败','--type','Text')
        Ui @('wait-for','InstallButton','-p','IsEnabled','--value','True')
        Ui @('screenshot','--output',"$evidence/update-error.png")
    }
    Check 'Navigation back restores a painted plugin center' {
        Ui @('invoke','SettingsNav.general')
        Ui @('invoke','SettingsNav.plugins')
        Ui @('wait-for','PluginCenter.Results','--value','8 个插件','--contains')
        Ui @('wait-for','Plugin.preview-0.Enabled')
    }
    Check 'Source dialog cancels without changing the configured source' {
        Ui @('invoke','PluginCenter.Options')
        Ui @('invoke','PluginCenter.Source')
        Ui @('wait-for','BaseUrlBox')
        Ui @('invoke','CloseButton')
        Ui @('wait-for','PluginCenter.Results','--value','本机目录','--contains')
    }
} finally {
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$evidence/results.json" -Encoding utf8
    & winapp ui yield | Out-Null
}
$results | Format-Table name,status,detail -Wrap
if ($results.status -contains 'FAIL') { exit 1 }
