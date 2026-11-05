param([switch]$Test)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot 'backend')
try {
    go build -o temporalbim.exe .
    if ($LASTEXITCODE -ne 0) { throw '後端建置失敗。' }
} finally { Pop-Location }
Push-Location (Join-Path $PSScriptRoot 'revit-plugin')
try {
    dotnet build .\TemporalBim.csproj -c Release.R24
    if ($LASTEXITCODE -ne 0) { throw 'Revit 2024 外掛建置失敗。' }
} finally { Pop-Location }
if ($Test) { & (Join-Path $PSScriptRoot 'scripts\regression.ps1') }
Write-Output '完成建置。請執行 revit-plugin\install.ps1 -Version 2024 安裝，重新開啟 Revit 後生效。'
