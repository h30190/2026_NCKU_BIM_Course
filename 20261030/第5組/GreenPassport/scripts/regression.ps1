$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

& (Join-Path $PSScriptRoot 'verify-door-appearance.ps1')

Push-Location (Join-Path $root "backend")
try {
    go test ./...
    if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed.' }
} finally { Pop-Location }

Push-Location (Join-Path $root "revit-plugin")
try {
    dotnet build .\TemporalBim.csproj -c Release.R24
    if ($LASTEXITCODE -ne 0) { throw 'Revit 2024 build failed.' }
} finally { Pop-Location }

& (Join-Path $PSScriptRoot 'verify-uninstall.ps1')

Write-Host "REGRESSION PASS: backend workflow/rules/API, material policy, recoverable uninstall and Revit 2024 compilation"
