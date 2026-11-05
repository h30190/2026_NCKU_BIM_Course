# LEBR 碳排外掛：一鍵安裝／解除安裝（由「安裝外掛.bat」「解除安裝外掛.bat」呼叫）
# 自動找出電腦上的 Revit 2024～2027，把「已編譯外掛\Revit20xx」複製到該版本的增益集資料夾，並寫好 .addin。
param([switch]$Uninstall)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$prebuilt = Join-Path $root "已編譯外掛"
$installed = 2024..2027 | Where-Object { Test-Path "C:\Program Files\Autodesk\Revit $_\Revit.exe" }

function Wait-RevitClosed {
    while (Get-Process Revit -ErrorAction SilentlyContinue) {
        Write-Host ""
        Write-Host "Revit 還開著。請先存檔並關閉所有 Revit 視窗，關好後按 Enter 繼續。" -ForegroundColor Yellow
        [void](Read-Host)
    }
}

Write-Host "=== LEBR 碳排外掛 ===" -ForegroundColor Cyan
if (-not $installed) {
    Write-Host "這台電腦找不到 Revit 2024～2027（預設安裝位置 C:\Program Files\Autodesk）。" -ForegroundColor Red
    exit 1
}
Write-Host ("找到 Revit：" + ($installed -join "、"))
Wait-RevitClosed

foreach ($v in $installed) {
    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
    $target = Join-Path $addins "LebrCarbon"
    $manifest = Join-Path $addins "LebrCarbon.addin"

    if ($Uninstall) {
        Get-ChildItem $addins -Directory -Filter "LebrCarbon*" -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
        Remove-Item $manifest -Force -ErrorAction SilentlyContinue
        Write-Host "Revit $v：已移除" -ForegroundColor Green
        continue
    }

    $src = Join-Path $prebuilt "Revit$v"
    if (-not (Test-Path (Join-Path $src "LebrCarbon.dll"))) {
        Write-Host "Revit $v：找不到「已編譯外掛\Revit$v」，略過。你下載的可能是 GitHub 原始碼，裡面沒有編譯好的外掛。請到 https://github.com/017-YiChing/2026_NCKU_BIM_Course_Group-1/releases/tag/lebr-midterm-20261102 下載 LEBR-addin-package-20261102.zip，解壓縮後再雙擊「安裝外掛.bat」。" -ForegroundColor Yellow
        continue
    }
    New-Item -ItemType Directory -Force $addins | Out-Null
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $src $target -Recurse
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>LEBR 碳排計算</Name>
    <Assembly>$target\LebrCarbon.dll</Assembly>
    <FullClassName>LebrCarbon.App</FullClassName>
    <AddInId>3C9B4F1E-7A2D-4B8E-9F61-2D5A8C0E4B17</AddInId>
    <VendorId>LEBR</VendorId>
    <VendorDescription>低碳（低蘊含碳）建築評估 LEBR 計算外掛</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
    [System.IO.File]::WriteAllText($manifest, $xml, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Revit $v：安裝完成" -ForegroundColor Green
}

Write-Host ""
if ($Uninstall) {
    Write-Host "已解除安裝。下次開 Revit 就不會出現 LEBR 頁籤。"
} else {
    Write-Host "安裝好了！接下來："
    Write-Host "  1. 開啟 Revit。"
    Write-Host "  2. 跳出「無法驗證此增益集的發佈者」時，選「永遠載入」。"
    Write-Host "  3. 上方功能區會多一個「LEBR」頁籤。"
}
