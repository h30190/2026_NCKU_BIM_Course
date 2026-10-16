# 冒煙測試：起一個乾淨後端（臨時 port＋臨時資料夾，不動開發環境），
# 打一輪 API 全鏈路，過了才算能跑。課上改完程式先跑這個。
#
# 用法：在 sample 資料夾執行：
#   .\scripts\smoke.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$port = 18080
$data = Join-Path ([IO.Path]::GetTempPath()) ("rfi-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $data | Out-Null

$failed = 0
function Check($name, $cond, $detail = "") {
  if ($cond) { Write-Host "[PASS] $name" -ForegroundColor Green }
  else { Write-Host "[FAIL] $name $detail" -ForegroundColor Red; $script:failed++ }
}

# 起後端（背景，log 丟掉）
$backend = Start-Process -FilePath "go" -ArgumentList "run", ".", "--addr", "127.0.0.1:$port", "--data", $data `
  -WorkingDirectory (Join-Path $root "backend") -WindowStyle Hidden -PassThru
try {
  $base = "http://127.0.0.1:$port"
  # 等待啟動（最多 90 秒，第一次編譯較久）
  $up = $false
  for ($i = 0; $i -lt 90; $i++) {
    try {
      if ((Invoke-WebRequest -Uri "$base/api/health" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { $up = $true; break }
    } catch { Start-Sleep -Seconds 1 }
  }
  Check "後端啟動" $up
  if (-not $up) { throw "backend not up" }

  # 登入 seed admin
  $login = Invoke-WebRequest -Uri "$base/api/login" -Method Post `
    -Body '{"username":"admin","password":"changeme"}' -ContentType "application/json" `
    -UseBasicParsing -TimeoutSec 10 | Select-Object -ExpandProperty Content | ConvertFrom-Json
  Check "admin 登入" ($login.token.Length -gt 0)
  $h = @{ Authorization = "Bearer $($login.token)" }

  # 建 member＋viewer
  $r = Invoke-WebRequest -Uri "$base/api/users" -Method Post `
    -Body '{"username":"s01","password":"pw123","role":"member"}' -ContentType "application/json" `
    -Headers $h -UseBasicParsing -TimeoutSec 10
  Check "建 member" ($r.StatusCode -eq 201)
  $r = Invoke-WebRequest -Uri "$base/api/users" -Method Post `
    -Body '{"username":"g1","password":"pw123","role":"viewer"}' -ContentType "application/json" `
    -Headers $h -UseBasicParsing -TimeoutSec 10
  Check "建 viewer" ($r.StatusCode -eq 201)

  # 建議題（帶視圖＋模型名）
  $issue = Invoke-WebRequest -Uri "$base/api/issues" -Method Post -Body @{
    title = "冒煙測試"; description = "smoke"; reporter = "admin"; source = "revit"
    revit_element_id = "1"; view_name = "V"; revit_view_id = "u"; model_name = "m.rvt"
  } -Headers $h -UseBasicParsing -TimeoutSec 10 | Select-Object -ExpandProperty Content | ConvertFrom-Json
  Check "建議題" ($issue.id -gt 0 -and $issue.model_name -eq "m.rvt")

  # 指派＋改狀態
  $p = Invoke-WebRequest -Uri "$base/api/issues/$($issue.id)" -Method Patch `
    -Body '{"assignee":"s01","status":"in_progress"}' -ContentType "application/json" `
    -Headers $h -UseBasicParsing -TimeoutSec 10 | Select-Object -ExpandProperty Content | ConvertFrom-Json
  Check "指派＋改狀態" ($p.assignee -eq "s01" -and $p.status -eq "in_progress")

  # 封存隱藏／找回
  Invoke-WebRequest -Uri "$base/api/issues/$($issue.id)" -Method Patch `
    -Body '{"archived":true}' -ContentType "application/json" `
    -Headers $h -UseBasicParsing -TimeoutSec 10 | Out-Null
  $def = Invoke-WebRequest -Uri "$base/api/issues" -UseBasicParsing -TimeoutSec 10 | Select-Object -ExpandProperty Content | ConvertFrom-Json
  Check "封存後預設隱藏" (@($def).Count -eq 0)
  $arc = Invoke-WebRequest -Uri "$base/api/issues?archived=1" -UseBasicParsing -TimeoutSec 10 | Select-Object -ExpandProperty Content | ConvertFrom-Json
  Check "archived=1 找得回" (@($arc).Count -eq 1)

  # 未登入寫入擋下
  try {
    Invoke-WebRequest -Uri "$base/api/issues/$($issue.id)" -Method Patch `
      -Body '{"status":"closed"}' -ContentType "application/json" -UseBasicParsing -TimeoutSec 10 | Out-Null
    Check "未登入寫入擋下" $false "(居然成功)"
  } catch {
    Check "未登入寫入擋下" ($_.Exception.Response.StatusCode.value__ -eq 401)
  }

  # viewer 寫入擋下
  $vl = Invoke-WebRequest -Uri "$base/api/login" -Method Post `
    -Body '{"username":"g1","password":"pw123"}' -ContentType "application/json" `
    -UseBasicParsing -TimeoutSec 10 | Select-Object -ExpandProperty Content | ConvertFrom-Json
  try {
    Invoke-WebRequest -Uri "$base/api/issues/$($issue.id)" -Method Patch `
      -Body '{"status":"closed"}' -ContentType "application/json" `
      -Headers @{ Authorization = "Bearer $($vl.token)" } -UseBasicParsing -TimeoutSec 10 | Out-Null
    Check "viewer 寫入擋下" $false "(居然成功)"
  } catch {
    Check "viewer 寫入擋下" ($_.Exception.Response.StatusCode.value__ -eq 403)
  }
}
finally {
  if ($backend -and -not $backend.HasExited) { Stop-Process -Id $backend.Id -Force -ErrorAction SilentlyContinue }
  Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
}

if ($failed -gt 0) { Write-Host "SMOKE FAIL: $failed 項" -ForegroundColor Red; exit 1 }
Write-Host "SMOKE PASS：全鏈路正常" -ForegroundColor Green
