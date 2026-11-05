# 移除 Temporal BIM 外掛（跟 install.ps1 配對）：
#   .\uninstall.ps1 -Version 2024
# 注意：Revit 要先關掉，不然 DLL 被咬住刪不掉。
param([string]$Version = "2024")
$ErrorActionPreference = "Stop"
$addinDir = "$env:APPDATA\Autodesk\Revit\Addins\$Version"
$manifest = Join-Path $addinDir "TemporalBim.addin"
$pluginDir = Join-Path $addinDir "TemporalBimCurrent"
$legacyPluginDir = Join-Path $addinDir "TemporalBim"
if (Test-Path $manifest) { Remove-Item -LiteralPath $manifest -Force; Write-Host "已刪 $manifest" }
if (Test-Path $pluginDir) { Remove-Item -LiteralPath $pluginDir -Recurse -Force; Write-Host "已刪 $pluginDir" }
if (Test-Path $legacyPluginDir) { Remove-Item -LiteralPath $legacyPluginDir -Recurse -Force; Write-Host "已刪舊版 $legacyPluginDir" }
Write-Host "完成，重開 Revit $Version 確認分頁消失" -ForegroundColor Green
