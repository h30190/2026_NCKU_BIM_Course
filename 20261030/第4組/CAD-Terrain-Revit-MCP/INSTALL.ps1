param([string]$PythonExe)
$ErrorActionPreference='Stop'
if (!$PythonExe) { $PythonExe=Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' }
if (!(Test-Path -LiteralPath $PythonExe)) { throw 'Supply -PythonExe with a Python 3.12 x64 path.' }
& $PythonExe -c "import sys,struct,tkinter; assert sys.version_info[:2]==(3,12) and struct.calcsize('P')==8"
if ($LASTEXITCODE -ne 0) { throw 'Python 3.12 x64 with tkinter is required.' }

$assembly=Join-Path $PSScriptRoot 'bin\CadTerrainKitV3Icons.dll'
if (!(Test-Path -LiteralPath $assembly)) { throw 'Missing DLL. Run BUILD.ps1 first.' }
$addinFolder=Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2024'
New-Item -ItemType Directory -Path $addinFolder -Force | Out-Null
$target=Join-Path $addinFolder 'CADTerrainMCP.addin'
if (Test-Path -LiteralPath $target) {
    [xml]$existing=Get-Content -LiteralPath $target -Raw
    if ($existing.RevitAddIns.AddIn.AddInId -ne '1D083B6B-ADDD-4821-8A84-4051945C9CE8') { throw 'Unrecognized add-in; no changes made.' }
    Copy-Item -LiteralPath $target -Destination ($target+'.backup-'+(Get-Date -Format 'yyyyMMddHHmmss'))
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'python-path.txt'),$PythonExe,[Text.UTF8Encoding]::new($false))
$escaped=[System.Security.SecurityElement]::Escape($assembly)
$xml=@"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns><AddIn Type="Application"><Name>CAD Terrain MCP Kit v3</Name><Assembly>$escaped</Assembly><AddInId>1D083B6B-ADDD-4821-8A84-4051945C9CE8</AddInId><FullClassName>CadTerrainKit.App</FullClassName><VendorId>CTMK</VendorId><VendorDescription>CAD terrain demonstration kit</VendorDescription></AddIn></RevitAddIns>
"@
[IO.File]::WriteAllText($target,$xml,[Text.UTF8Encoding]::new($false))
Write-Host 'Installed v3 for NEXT Revit start. Save your work, close and reopen Revit. No running model was changed.'
