$ErrorActionPreference = "Stop"
$backend = Join-Path $PSScriptRoot "backend"
$exe = Join-Path $backend "temporalbim.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "找不到 $exe，請先在 backend 執行 go build -o temporalbim.exe ." }

$listener = Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort 8080 -State Listen -ErrorAction SilentlyContinue
if ($listener) {
    Write-Host "127.0.0.1:8080 已有服務監聽；未重複啟動。PID: $($listener.OwningProcess)"
    exit 0
}

$logDir = Join-Path $backend "logs"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$process = Start-Process -FilePath $exe -WorkingDirectory $backend -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $logDir "backend.log") `
    -RedirectStandardError (Join-Path $logDir "backend-error.log")
Write-Host "Green Passport 後端已啟動。PID: $($process.Id)｜http://127.0.0.1:8080/dashboard"
