param([ValidateSet('2022','2023','2024','2025','2026','2027')][string]$RevitVersion = '2022')
$ErrorActionPreference = 'Stop'
$shaftWorkspace = Split-Path $PSScriptRoot -Parent
$shaftProject = Join-Path $shaftWorkspace 'ShaftChecker-main\src\ShaftChecker.Revit\ShaftChecker.Revit.csproj'
& dotnet build $shaftProject -c Release "-p:RevitVersion=$RevitVersion" -p:DeployToRevit=false --nologo
if ($LASTEXITCODE -ne 0) { throw 'ShaftChecker build failed. Close Revit if its DLLs are in use, then retry.' }
Write-Output "ShaftChecker for Revit $RevitVersion is ready. Restart Revit and use the ShaftChecker button on the CaseBase ribbon."
