# LebrCarbon 網頁控制台一鍵啟動（沿用 20261016 RFI 範例的 start.ps1）
#
# 用法：在 console 資料夾按右鍵 →「使用 PowerShell 執行」，或：
#   .\start.ps1
#
# 會開兩個視窗：一個跑後端（:8080，只綁 loopback，不跳防火牆），一個跑前端（:5173）。
# 資料庫是空的時候，後端會自動匯入 samples\ 裡的範例快照，教室沒有 Revit 2026 也能 demo。
# 跑起來後瀏覽器開 http://localhost:5173；關掉兩個視窗即停止。
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

foreach ($cmd in @("go", "node", "npm")) {
  if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
    Write-Host "缺 $cmd，請先安裝（與 20261016 RFI 範例的需求相同）" -ForegroundColor Red
    exit 1
  }
}

# 已經在跑的就跳過（Revit 的「網頁控制台」按鈕可能重複呼叫這支腳本）
function Test-Listening($port) {
  [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
}

if (Test-Listening 8080) {
  Write-Host "[1/2] 後端已在運行（:8080），略過" -ForegroundColor Yellow
} else {
  Write-Host "[1/2] 啟動後端（Go + SQLite，http://127.0.0.1:8080）..." -ForegroundColor Green
  Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root\backend'; go run . -seed '$root\samples'"
}

if (Test-Listening 5173) {
  Write-Host "[2/2] 前端已在運行（:5173），略過" -ForegroundColor Yellow
} else {
  Write-Host "[2/2] 啟動前端（React，http://localhost:5173）..." -ForegroundColor Green
  Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root\frontend'; if (-not (Test-Path node_modules)) { Write-Host '第一次跑，先裝依賴（約1分鐘）...'; npm install }; npm run dev"
}

Write-Host ""
Write-Host "兩個視窗都出現 ready / listening 後，瀏覽器開 http://localhost:5173" -ForegroundColor Cyan
Write-Host "要清空重來：關掉後端視窗，刪除 backend\data\lebr.db 再執行一次" -ForegroundColor Cyan
