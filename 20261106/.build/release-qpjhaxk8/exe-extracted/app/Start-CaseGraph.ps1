$ErrorActionPreference = 'Stop'
$caseGraphRoot = $PSScriptRoot
$caseGraphPython = Join-Path $caseGraphRoot 'runtime\python\pythonw.exe'
if (-not (Test-Path -LiteralPath $caseGraphPython)) {
    $caseGraphPython = Join-Path $caseGraphRoot 'backend\.venv\Scripts\pythonw.exe'
}
$caseGraphRunner = Join-Path $caseGraphRoot 'backend\run_backend.py'
$caseGraphRequiredApi = 7
if (-not (Test-Path -LiteralPath $caseGraphPython)) {
    throw 'CaseGraph backend environment is missing. See README.md for installation.'
}
try {
    $caseGraphHealth = Invoke-RestMethod -Uri 'http://127.0.0.1:8000/graph/health' -TimeoutSec 2
} catch { $caseGraphHealth = $null }
if ($caseGraphHealth -and $caseGraphHealth.service -eq 'casegraph') {
    if ($caseGraphHealth.api_version -ge $caseGraphRequiredApi) {
        Write-Output 'CaseGraph is already running at http://127.0.0.1:8000'
        exit 0
    }
    # An older service lacks current endpoints; replace it. Older versions only record their PID in the file.
    $caseGraphPid = if ($caseGraphHealth.pid) { $caseGraphHealth.pid } else { Get-Content -LiteralPath (Join-Path $caseGraphRoot 'backend\data\server.pid') }
    $caseGraphOld = Get-Process -Id $caseGraphPid -ErrorAction SilentlyContinue
    if ($caseGraphOld -and $caseGraphOld.ProcessName -match '^pythonw?$') {
        Write-Output ('Stopping outdated CaseGraph service (API ' + $caseGraphHealth.api_version + ', PID ' + $caseGraphPid + ')')
        Stop-Process -Id $caseGraphPid -Force
        $caseGraphOld.WaitForExit(5000) | Out-Null
    } elseif ($caseGraphOld) {
        throw ('Outdated CaseGraph service PID ' + $caseGraphPid + ' is not a Python process; stop it manually.')
    }
}
$caseGraphProcess = Start-Process -FilePath $caseGraphPython -ArgumentList ('"' + $caseGraphRunner + '"') -WorkingDirectory $caseGraphRoot -WindowStyle Hidden -PassThru
for ($caseGraphAttempt = 0; $caseGraphAttempt -lt 20; $caseGraphAttempt++) {
    Start-Sleep -Milliseconds 500
    try {
        $caseGraphHealth = Invoke-RestMethod -Uri 'http://127.0.0.1:8000/graph/health' -TimeoutSec 2
        if ($caseGraphHealth.service -eq 'casegraph' -and $caseGraphHealth.api_version -ge $caseGraphRequiredApi) {
            Write-Output ('CaseGraph started at http://127.0.0.1:8000 (PID ' + $caseGraphHealth.pid + ')')
            exit 0
        }
    } catch { }
    if ($caseGraphProcess.HasExited) { break }
}
throw 'CaseGraph did not start. Check backend/data/server.log and whether port 8000 is in use.'
