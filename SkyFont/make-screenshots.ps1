<#
    SkyFont 文档截图生成脚本

    用法：
        .\make-screenshots.ps1
        .\make-screenshots.ps1 -Exe .\publish\SkyFont.exe

    流程：
      1. 缺 sample-fonts 时，从系统字体目录复制 8 个字体当演示字体库
      2. 备份 %APPDATA%\SkyFont\settings.json，临时改成截图状态
         （字体库指向 sample-fonts、窗口 1360x860、示例文本用默认三行）
      3. 调 SkyFont.exe --shot 生成三张图到 docs\
      4. 无论成功失败都还原原设置

    注意：截图会读取 settings.json 里的窗口尺寸和字体库，所以第 2 步不能省。
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$Docs
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
if (-not $Exe)  { $Exe  = Join-Path $root "SkyFont.exe" }
if (-not $Docs) { $Docs = Join-Path $root "docs" }

if (-not (Test-Path $Exe))  { throw "找不到 $Exe，请先构建或在 -Exe 指定路径" }
if (-not (Test-Path $Docs)) { New-Item -ItemType Directory -Path $Docs | Out-Null }

$sampleDir = Join-Path $root "sample-fonts"
$settingsPath = Join-Path $env:APPDATA "SkyFont\settings.json"
$backupPath = Join-Path $env:TEMP "skyfont-settings-backup.json"

# ---- 1. 演示字体库 ----
$samples = @(
    "BROADW.TTF", "BRUSHSCI.TTF", "FREESCPT.TTF", "JUICE___.TTF",
    "MARLETT.TTF", "MTEXTRA.TTF", "GABRIOLA.TTF", "INKFREE.TTF"
)

if (-not (Test-Path $sampleDir)) { New-Item -ItemType Directory -Path $sampleDir | Out-Null }

$copied = 0
foreach ($name in $samples) {
    $target = Join-Path $sampleDir $name
    if (Test-Path $target) { continue }

    $source = Join-Path $env:WINDIR "Fonts\$name"
    if (Test-Path $source) {
        Copy-Item $source $target
        $copied++
    }
    else {
        Write-Warning "系统字体里没有 $name，已跳过"
    }
}

if ($copied -gt 0) { Write-Host "已从系统字体复制 $copied 个样例字体到 sample-fonts" -ForegroundColor DarkGray }

# ---- 2. 临时设置 ----
$hadSettings = Test-Path $settingsPath
if ($hadSettings) {
    Copy-Item $settingsPath $backupPath -Force
    $settings = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
else {
    $settings = [pscustomobject]@{}
}

function Set-Field($object, $name, $value) {
    if ($object.PSObject.Properties.Name -contains $name) { $object.$name = $value }
    else { $object | Add-Member -NotePropertyName $name -NotePropertyValue $value }
}

Set-Field $settings "FontFolders" @($sampleDir)
Set-Field $settings "WindowWidth" 1360
Set-Field $settings "WindowHeight" 860
Set-Field $settings "WindowMaximized" $false
Set-Field $settings "PreviewPaneWidth" 440
Set-Field $settings "PreviewSize" 32
Set-Field $settings "PreviewBold" $false
Set-Field $settings "PreviewItalic" $false
Set-Field $settings "PreviewWrap" $true
Set-Field $settings "SampleText" "The quick brown fox jumps over the lazy dog.`n敏捷的棕狐狸跳过懒狗。`n0123456789 ©®™"
Set-Field $settings "SortMode" "Family"
Set-Field $settings "RecursiveScan" $true
Set-Field $settings "DeactivateOnExit" $true
Set-Field $settings "LastActivePaths" @()

New-Item -ItemType Directory -Path (Split-Path $settingsPath) -Force | Out-Null
$settings | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8

# ---- 3. 截图 ----
# SkyFont 是 WinExe（GUI 子系统），用 & 调用 PowerShell 不会等待、也拿不到退出码，
# 必须显式起进程并 WaitForExit。
function Invoke-Shot([string]$exePath, [string]$outputPath, [string]$pane) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $exePath
    $info.Arguments = '--shot "{0}" "{1}"' -f $outputPath, $pane
    $info.UseShellExecute = $false

    $proc = [System.Diagnostics.Process]::Start($info)
    $proc.WaitForExit(120000) | Out-Null
    if (-not $proc.HasExited) { $proc.Kill(); throw "截图超时：$outputPath" }

    return $proc.ExitCode
}

$targets = @(
    @{ File = "screenshot-main.png";   Pane = "preview" },
    @{ File = "screenshot-active.png"; Pane = "active" },
    @{ File = "screenshot-chars.png";  Pane = "chars#cjk@yahei" }
)

try {
    foreach ($target in $targets) {
        $output = Join-Path $Docs $target.File
        Write-Host ("生成 {0}  (pane = {1})" -f $target.File, $target.Pane) -ForegroundColor Cyan

        $code = Invoke-Shot $Exe $output $target.Pane
        if ($code -ne 0) { throw "截图失败：$($target.File)（退出码 $code）" }
        if (-not (Test-Path $output)) { throw "没有生成 $output" }

        $size = [math]::Round((Get-Item $output).Length / 1KB, 0)
        Write-Host ("  -> {0} KB" -f $size) -ForegroundColor DarkGray
    }
}
finally {
    # ---- 4. 还原设置 ----
    if ($hadSettings) {
        Copy-Item $backupPath $settingsPath -Force
        Remove-Item $backupPath -Force -ErrorAction SilentlyContinue
        Write-Host "settings.json 已还原" -ForegroundColor Green
    }
    else {
        Remove-Item $settingsPath -Force -ErrorAction SilentlyContinue
        Write-Host "原本没有 settings.json，已清除临时文件" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "完成，产物在 $Docs" -ForegroundColor Green
