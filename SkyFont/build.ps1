<#
    SkyFont 构建脚本 —— 目标：只产出一个 SkyFont.exe，没有 .dll / .json / .pdb 伴生文件

    要点：单文件打包只在 `dotnet publish` 阶段生效（PublishSingleFile）。
          `dotnet build` 或 Visual Studio 的「生成」永远会得到
          SkyFont.exe(apphost) + SkyFont.dll(真正的代码) + deps.json + runtimeconfig.json + pdb，
          那是编译中间产物，不是用于分发的成品。

    用法：
        .\build.ps1                     框架依赖单文件（约 370 KB，需装 .NET 9 桌面运行时）
        .\build.ps1 -SelfContained      自包含单文件（约 60 MB，任意 x64 Windows 直接跑）
        .\build.ps1 -Runtime win-arm64  指定目标架构
        .\build.ps1 -Clean              顺带清理 bin / obj 中间产物
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [string]$Runtime = "win-x64",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$root    = $PSScriptRoot
$project = Join-Path $root "src\SkyFont\SkyFont.csproj"
$output  = Join-Path $root "publish"

Write-Host "== SkyFont 构建 ==" -ForegroundColor Cyan
Write-Host "项目 : $project"
Write-Host "架构 : $Runtime"
if ($SelfContained) { Write-Host "模式 : 自包含（不需要目标机装运行时）" }
else                { Write-Host "模式 : 框架依赖（目标机需装 .NET 9 桌面运行时）" }

if ($Clean) {
    Write-Host "清理 : bin / obj" -ForegroundColor DarkGray
    foreach ($d in @("src\SkyFont\bin", "src\SkyFont\obj")) {
        $p = Join-Path $root $d
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    }
}

# publish 目录必须是干净的，否则上一轮的残留文件会混进「成品」里
if (Test-Path $output) { Remove-Item $output -Recurse -Force }

$pubArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", $SelfContained.IsPresent.ToString().ToLowerInvariant(),
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "-p:SatelliteResourceLanguages=en",
    "-o", $output
)
if ($SelfContained) { $pubArgs += "-p:EnableCompressionInSingleFile=true" }

& dotnet @pubArgs
if ($LASTEXITCODE -ne 0) { throw "构建失败（dotnet 退出码 $LASTEXITCODE）" }

$exe = Join-Path $output "SkyFont.exe"
if (-not (Test-Path $exe)) { throw "未找到输出文件 $exe" }

# 断言目录里只有这一个文件 —— 出现别的就说明单文件打包没生效
$strays = @(Get-ChildItem $output -File | Where-Object { $_.Name -ne "SkyFont.exe" })
if ($strays.Count -gt 0) {
    Write-Warning "输出目录出现伴生文件，说明单文件打包未完全生效："
    $strays | ForEach-Object { Write-Warning ("  - " + $_.Name) }
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 2)
Write-Host ""
Write-Host ("构建完成: $exe ($size MB)") -ForegroundColor Green
if ($strays.Count -eq 0) { Write-Host "输出目录仅含 1 个文件 [OK]" -ForegroundColor Green }
