param(
    [Parameter(Mandatory = $true)][string]$ProjectDir,
    [string]$Configuration = "Debug",
    [string]$OutputDir = "dist"
)

$ErrorActionPreference = "Stop"

$projectDir = Resolve-Path $ProjectDir
$manifestPath = Join-Path $projectDir "plugin.json"
if (-not (Test-Path $manifestPath)) {
    throw "缺少 plugin.json：$manifestPath"
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if (-not $manifest.id -or -not $manifest.entry_dll) {
    throw "plugin.json 必须包含 id 与 entry_dll"
}

$buildDir = Join-Path $projectDir "bin\$Configuration\net10.0-windows10.0.26100.0\win-x64"
if (-not (Test-Path (Join-Path $buildDir $manifest.entry_dll))) {
    throw "未找到构建输出，请先 dotnet build -c $Configuration：$buildDir"
}

$staging = Join-Path $projectDir "obj\pack-staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

# Windows App SDK / WinUI / TraceEvent 工具链由宿主提供或用不到，不进插件包
$skipNames = @(
    "WinIsland.Core.dll",
    "Microsoft.WinUI.dll",
    "Microsoft.Windows.SDK.NET.dll",
    "WinRT.Runtime.dll",
    "WebView2Loader.dll",
    "Microsoft.ML.OnnxRuntime.dll",
    "System.Numerics.Tensors.dll",
    "Microsoft.Security.Authentication.OAuth.Projection.dll",
    "Dia2Lib.dll",
    "TraceReloggerLib.dll",
    "KernelTraceControl.dll",
    "msdia140.dll"
)
$skipPatterns = @(
    "Microsoft.Windows.*.dll",
    "Microsoft.Web.WebView2*.dll",
    "Microsoft.Graphics.*.dll",
    "Microsoft.InteractiveExperiences.*.dll",
    "Microsoft.*.Projection.dll",
    "Microsoft.WindowsAppRuntime*"
)

Get-ChildItem $buildDir -Recurse -File |
    Where-Object { $_.Extension -notin ".pdb" } |
    Where-Object { $skipNames -notcontains $_.Name } |
    Where-Object { $skip = $false; foreach ($p in $skipPatterns) { if ($_.Name -like $p) { $skip = $true } }; -not $skip } |
    ForEach-Object {
        $relative = $_.FullName.Substring($buildDir.Length).TrimStart('\')
        $target = Join-Path $staging $relative
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item $_.FullName $target -Force
    }

Copy-Item $manifestPath (Join-Path $staging "plugin.json") -Force

$outputRoot = Join-Path (Resolve-Path $projectDir).Path "..\$OutputDir"
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$packagePath = Join-Path $outputRoot "$($manifest.id).lwp"
if (Test-Path $packagePath) { Remove-Item $packagePath -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $packagePath)
Remove-Item $staging -Recurse -Force

$size = [math]::Round((Get-Item $packagePath).Length / 1KB, 1)
Write-Output "已生成插件包：$packagePath ($size KB)"
