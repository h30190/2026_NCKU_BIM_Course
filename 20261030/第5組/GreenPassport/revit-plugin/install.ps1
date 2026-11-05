# 部署（傳統 .addin 模式，跟課程骨架同流程）：
#   dotnet build -c Release.R24
#   .\install.ps1 -Version 2024
#   跑完重開 Revit 2024 → "Temporal BIM" 分頁 → 「建立版本快照」/「歷史追溯面板」
#
# 跟骨架的 install.ps1 不一樣的地方：這裡多裝了 Newtonsoft.Json.dll，
# 因為我們用它解析 Diff 的巢狀 JSON（骨架沒有外部套件，只有單一 dll）。
param([string]$Version = "2024")
$ErrorActionPreference = "Stop"
$addinDir = "$env:APPDATA\Autodesk\Revit\Addins\$Version"
New-Item -ItemType Directory -Force $addinDir | Out-Null
$cfg = "Release.R$($Version.Substring(2))"
$outDir = Join-Path $PSScriptRoot "bin\$cfg\net48"
$dll = Join-Path $outDir "TemporalBim.dll"
if (-not (Test-Path $dll)) { throw "找不到 $dll，先跑 dotnet build -c $cfg" }

# A running Revit locks loaded DLLs. Deploy each build to its own directory,
# then switch the manifest only after all required files are present.
$buildHash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.Substring(0, 16)
$relativePluginDir = "TemporalBimReleases\$buildHash"
$pluginDir = Join-Path $addinDir $relativePluginDir
New-Item -ItemType Directory -Force $pluginDir | Out-Null
$installedDll = Join-Path $pluginDir "TemporalBim.dll"
if (-not (Test-Path -LiteralPath $installedDll)) {
    Copy-Item -LiteralPath $dll -Destination $installedDll
} elseif ((Get-FileHash -LiteralPath $installedDll).Hash -ne (Get-FileHash -LiteralPath $dll).Hash) {
    throw "版本資料夾的 DLL 與來源不符：$installedDll"
}

$newtonsoft = Join-Path $outDir "Newtonsoft.Json.dll"
if (Test-Path $newtonsoft) {
    $installedNewtonsoft = Join-Path $pluginDir "Newtonsoft.Json.dll"
    if (-not (Test-Path -LiteralPath $installedNewtonsoft)) {
        Copy-Item -LiteralPath $newtonsoft -Destination $installedNewtonsoft
    } elseif ((Get-FileHash -LiteralPath $installedNewtonsoft).Hash -ne (Get-FileHash -LiteralPath $newtonsoft).Hash) {
        throw "版本資料夾的 Newtonsoft.Json.dll 與來源不符"
    }
} else {
    throw "找不到 Newtonsoft.Json.dll，請先確認有跑過 dotnet build"
}

[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot "TemporalBim.addin") -Raw -Encoding UTF8
$manifest.RevitAddIns.AddIn.Assembly = "$relativePluginDir\TemporalBim.dll"
$manifest.Save((Join-Path $addinDir "TemporalBim.addin"))

Write-Host "已部署到 $pluginDir，重開 Revit $Version 即可使用" -ForegroundColor Green
