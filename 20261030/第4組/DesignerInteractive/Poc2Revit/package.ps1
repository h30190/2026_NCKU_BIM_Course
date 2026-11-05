param([string]$PackageId = ([guid]::NewGuid().ToString('N')))
$ErrorActionPreference = 'Stop'
if ($PackageId -notmatch '^[a-f0-9]{32}$') { throw 'PackageId must be a new 32-character lower-case GUID.' }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$packageRoot = Join-Path $PSScriptRoot "Bootstrap/bin/packages/$PackageId"
if (Test-Path -LiteralPath $packageRoot) { throw 'Package exists; never overwrite candidate evidence.' }
& dotnet build (Join-Path $PSScriptRoot 'Bootstrap/Poc2Revit.Bootstrap.csproj') -c Release --nologo -v minimal "-p:PocBootstrapSuffix=.$PackageId"
if ($LASTEXITCODE -ne 0) { throw 'Candidate build failed.' }
New-Item -ItemType Directory -Path $packageRoot | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Host/bin/Release/net10.0-windows') -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name.EndsWith('.deps.json') } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $packageRoot }
$bootstrap = "EVCanopyDesigner.Poc2.Revit.Bootstrap.$PackageId.dll"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Bootstrap/bin/Release/net10.0-windows/$bootstrap") -Destination $packageRoot
$inputs = @{
 'selected-canopy.json' = 'SitePoc/Circulation/selected-canopy.json'
 'proposal.json' = 'docs/previews/POC2Circulation-20261026-refined/proposal.json'
 'maneuver-review.json' = 'docs/previews/POC2Circulation-20261026-final-review/review.json'
}
foreach ($name in $inputs.Keys) { Copy-Item -LiteralPath (Join-Path $repoRoot $inputs[$name]) -Destination (Join-Path $packageRoot $name) }
@{ PackageId=$PackageId; WorkspaceRoot=$repoRoot; TestRoot=(Join-Path $repoRoot 'Revit_Test_Models/POC2StaticSite') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'candidate-settings.json') -Encoding utf8NoBOM
$addinId = [guid]::NewGuid().ToString()
$assemblyXml = [System.Security.SecurityElement]::Escape((Join-Path $packageRoot $bootstrap))
@"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns><AddIn Type="Application"><Name>EV Hub POC2 isolated $PackageId</Name><Assembly>$assemblyXml</Assembly><AddInId>$addinId</AddInId><FullClassName>EVCanopyDesigner.Poc2.Revit.Bootstrap.Poc2Application</FullClassName><VendorId>EVHB</VendorId><VendorDescription>Academic isolated static POC; no autorun</VendorDescription></AddIn></RevitAddIns>
"@ | Set-Content -LiteralPath (Join-Path $packageRoot "EVHubPoc2-$PackageId.addin") -Encoding utf8NoBOM
$hashes = [ordered]@{}
Get-ChildItem -LiteralPath $packageRoot -File | Sort-Object Name | ForEach-Object { $hashes[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'sha256.json') -Encoding utf8NoBOM
Write-Output "PACKAGED ONLY; NOT INSTALLED: $packageRoot"
Write-Output 'No Revit process, protected model, installed add-in or MCP configuration was changed.'
