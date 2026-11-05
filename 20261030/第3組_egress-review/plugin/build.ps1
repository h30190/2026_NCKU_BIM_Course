param(
    [ValidateSet('2024','2025','2026','2027')][string]$RevitVersion = '2027',
    [string]$Dotnet = 'dotnet',
    [string]$RevitDirectory = "C:\Program Files\Autodesk\Revit $RevitVersion"
)
$ErrorActionPreference = 'Stop'
# The review page is embedded in the DLL, so regenerate it first to pick up engine and page changes.
$projectRoot = Split-Path $PSScriptRoot -Parent
& node (Join-Path $projectRoot 'build.js')
if ($LASTEXITCODE -ne 0) { throw 'node build.js failed; Node.js is needed to build the add-in (not to use it).' }
& $Dotnet build (Join-Path $PSScriptRoot 'EgressExporter.csproj') -c Release "-p:RevitVersion=$RevitVersion" "-p:RevitDirectory=$RevitDirectory"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
