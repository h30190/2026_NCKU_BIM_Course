[CmdletBinding()]
param([string]$RevitInstallDir = 'C:/Program Files/Autodesk/Revit 2026')
$ErrorActionPreference='Stop'
$workspaceRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$packageId=[Guid]::NewGuid().ToString('N')
$packageRoot=Join-Path $workspaceRoot "DesignerInteractive/artifacts/packages/$packageId"
if(Test-Path -LiteralPath $packageRoot){throw 'Package directory already exists'}
$null=New-Item -ItemType Directory -Path $packageRoot
$lines= & dotnet build (Join-Path $workspaceRoot 'DesignerInteractive/RevitBootstrap') -c Release -warnaserror "-p:RevitInstallDir=$RevitInstallDir" "-p:PocBootstrapSuffix=.$packageId"
$code=$LASTEXITCODE
$lines | Set-Content (Join-Path $packageRoot 'release-build.txt') -Encoding utf8
if($code -ne 0){throw "Build failed: $code. No package/deployment accepted."}
$hostBin=Join-Path $workspaceRoot 'DesignerInteractive/RevitHost/bin/Release/net10.0-windows'
$bootstrapBin=Join-Path $workspaceRoot 'DesignerInteractive/RevitBootstrap/bin/Release/net10.0-windows'
foreach($source in @($hostBin,$bootstrapBin)){
 foreach($file in Get-ChildItem -LiteralPath $source -File | Where-Object {$_.Extension -in @('.dll','.json','.pdb')}){
  if($source -eq $bootstrapBin -and $file.BaseName -notin @("EVCanopyDesigner.Interactive.Revit.Bootstrap.$packageId","EVCanopyDesigner.Interactive.Revit.Bootstrap.$packageId.deps")){continue}
  if($file.Extension -eq '.dll' -and $file.Name -notmatch '^(EVCanopyDesigner|DesignerInteractive)'){throw "Unexpected third-party/API payload: $($file.Name)"}
  $destination=Join-Path $packageRoot $file.Name
  if(Test-Path -LiteralPath $destination){if((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash){throw "Dependency collision: $($file.Name)"};continue}
  [IO.File]::Copy($file.FullName,$destination,$false)
 }
}
$settings=[ordered]@{PackageId=$packageId;BootstrapAssembly="EVCanopyDesigner.Interactive.Revit.Bootstrap.$packageId.dll";WorkspaceRoot=$workspaceRoot;TestRoot=(Join-Path $workspaceRoot 'Revit_Test_Models/InteractiveDesignerPOC')}
$settings | ConvertTo-Json | Set-Content (Join-Path $packageRoot 'candidate-settings.json') -Encoding utf8
$apis=@('Revit.exe','RevitAPI.dll','RevitAPIUI.dll','RevitAPI.runtimeconfig.json') | ForEach-Object {$file=Get-Item -LiteralPath (Join-Path $RevitInstallDir $_);[ordered]@{File=$file.FullName;Length=$file.Length;Version=$file.VersionInfo.FileVersion;Sha256=(Get-FileHash -LiteralPath $file.FullName).Hash}}
[ordered]@{PackageId=$packageId;SourceCommit=(& git -C $workspaceRoot rev-parse HEAD);CreatedUtc=[DateTime]::UtcNow.ToString('O');HostApis=$apis;Scope='CanopyOnly';EquipmentGeneration=$false;RetainedEquipmentAccessConflicts=21;IsolatedDependencyContext=$true;NewModelsOnly=$true} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $packageRoot 'package.json') -Encoding utf8
$hashes=[ordered]@{}
Get-ChildItem -LiteralPath $packageRoot -File | Sort-Object Name | ForEach-Object {$hashes[$_.Name]=(Get-FileHash -LiteralPath $_.FullName).Hash}
$hashes | ConvertTo-Json | Set-Content (Join-Path $packageRoot 'sha256.json') -Encoding utf8
[pscustomobject]@{PackageId=$packageId;PackageRoot=$packageRoot;Status='BUILT_NOT_DEPLOYED';Files=$hashes.Count}
