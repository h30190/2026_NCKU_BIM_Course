# 回歸測試：三件全跑（後端 vet＋test、前端 build、外掛 build）。
# 有裝對應版 Revit 才編該配置；沒裝就跳過並說明（不算失敗）。
# smoke 測的是「跑起來對不對」，這個測的是「改完有沒有編壞」。
#
# 用法：在 sample 資料夾執行：
#   .\scripts\regress.ps1
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$failed = 0
$skipped = 0

Write-Host "=== [1/3] 後端 go vet + test ===" -ForegroundColor Cyan
Push-Location (Join-Path $root "backend")
go vet ./...
if ($LASTEXITCODE -ne 0) { $failed++; Write-Host "[FAIL] go vet" -ForegroundColor Red } else { Write-Host "[PASS] go vet" -ForegroundColor Green }
go test ./...
if ($LASTEXITCODE -ne 0) { $failed++; Write-Host "[FAIL] go test" -ForegroundColor Red } else { Write-Host "[PASS] go test" -ForegroundColor Green }
Pop-Location

Write-Host ""
Write-Host "=== [2/3] 前端 vite build ===" -ForegroundColor Cyan
Push-Location (Join-Path $root "frontend")
if (-not (Test-Path node_modules)) {
  Write-Host "缺 node_modules，先 npm install..."
  npm install --no-audit --no-fund --loglevel=error
}
npm run build
if ($LASTEXITCODE -ne 0) { $failed++; Write-Host "[FAIL] vite build" -ForegroundColor Red } else { Write-Host "[PASS] vite build" -ForegroundColor Green }
Pop-Location

Write-Host ""
Write-Host "=== [3/3] 外掛 dotnet build ===" -ForegroundColor Cyan
Push-Location (Join-Path $root "revit-plugin")
foreach ($ver in @("2024", "2026")) {
  if (Test-Path "C:\Program Files\Autodesk\Revit $ver\RevitAPI.dll") {
    dotnet build -c "Release.R$($ver.Substring(2))" --nologo -v q
    if ($LASTEXITCODE -ne 0) { $failed++; Write-Host "[FAIL] build R$ver" -ForegroundColor Red } else { Write-Host "[PASS] build R$ver" -ForegroundColor Green }
  } else {
    $skipped++; Write-Host "[SKIP] R$ver（本機沒裝 Revit $ver，到教室再驗）" -ForegroundColor Yellow
  }
}
Pop-Location

Write-Host ""
if ($failed -gt 0) { Write-Host "REGRESS FAIL: $failed 項失敗，$skipped 項跳過" -ForegroundColor Red; exit 1 }
Write-Host "REGRESS PASS（$skipped 項跳過）" -ForegroundColor Green
