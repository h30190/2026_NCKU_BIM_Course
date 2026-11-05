$ErrorActionPreference='Stop'
if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Save and close Revit first.' }
$target=Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2024\CADTerrainMCP.addin'
if (Test-Path -LiteralPath $target) {
    [xml]$manifest=Get-Content -LiteralPath $target -Raw
    if ($manifest.RevitAddIns.AddIn.AddInId -ne '1D083B6B-ADDD-4821-8A84-4051945C9CE8') { throw 'Unexpected manifest; refusing to remove it.' }
    Remove-Item -LiteralPath $target
}
Write-Host 'Removed only this add-in manifest. Models and package files were not deleted.'
