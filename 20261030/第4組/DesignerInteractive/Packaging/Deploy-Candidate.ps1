[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageRoot)
$ErrorActionPreference='Stop'
$workspaceRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$source=(Resolve-Path -LiteralPath $PackageRoot).Path
if(-not $source.StartsWith((Join-Path $workspaceRoot 'DesignerInteractive/artifacts/packages')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Package must be built in the dedicated workspace package directory'}
$settings=Get-Content -LiteralPath (Join-Path $source 'candidate-settings.json') -Raw | ConvertFrom-Json
if($settings.WorkspaceRoot -ne $workspaceRoot -or $settings.PackageId -notmatch '^[a-f0-9]{32}$'){throw 'Package identity mismatch'}
$hashes=Get-Content -LiteralPath (Join-Path $source 'sha256.json') -Raw | ConvertFrom-Json -AsHashtable
foreach($file in $hashes.Keys){if([IO.Path]::GetFileName($file) -ne $file -or (Get-FileHash -LiteralPath (Join-Path $source $file)).Hash -ne $hashes[$file]){throw "Package hash/name mismatch: $file"}}
$registrationRoot=Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2026'
$target=Join-Path $registrationRoot ('EVHub.Interactive.POC.'+$settings.PackageId)
$manifest=Join-Path $registrationRoot ('EVHub.Interactive.POC.'+$settings.PackageId+'.addin')
if((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $manifest)){throw 'Candidate installation target exists; no overwriting or unregistering is allowed'}
$before=[ordered]@{}
foreach($root in @($registrationRoot,'C:/ProgramData/Autodesk/Revit/Addins/2026')){if(Test-Path -LiteralPath $root){Get-ChildItem -LiteralPath $root -File -Recurse | ForEach-Object {$before[$_.FullName]=(Get-FileHash -LiteralPath $_.FullName).Hash}}}
$null=New-Item -ItemType Directory -Path $target
foreach($file in Get-ChildItem -LiteralPath $source -File){[IO.File]::Copy($file.FullName,(Join-Path $target $file.Name),$false)}
foreach($file in $hashes.Keys){if((Get-FileHash -LiteralPath (Join-Path $target $file)).Hash -ne $hashes[$file]){throw 'Copied package verification failed'}}
$assembly=[Security.SecurityElement]::Escape((Join-Path $target $settings.BootstrapAssembly))
$id=[Guid]::ParseExact($settings.PackageId,'N').ToString('D')
$xml=@"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns><AddIn Type="Application"><Name>EV Hub Isolated Interactive POC $($settings.PackageId)</Name><Assembly>$assembly</Assembly><AddInId>$id</AddInId><FullClassName>EVCanopyDesigner.Interactive.Revit.Bootstrap.PocApplication</FullClassName><VendorId>EVCD</VendorId><VendorDescription>Isolated canopy-only POC; NEW disposable projects only</VendorDescription></AddIn></RevitAddIns>
"@
$stream=[IO.FileStream]::new($manifest,[IO.FileMode]::CreateNew)
try{$writer=[IO.StreamWriter]::new($stream,[Text.UTF8Encoding]::new($false));$writer.Write($xml);$writer.Flush()}finally{$stream.Dispose()}
foreach($file in $before.Keys){if(-not (Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne $before[$file]){throw "Existing installed file changed: $file. Stop before Revit/model operations."}}
$receipt=[ordered]@{Status='DEPLOYED_SIDE_BY_SIDE';PackageId=$settings.PackageId;PackageSource=$source;InstalledDirectory=$target;NewManifest=$manifest;ManifestSha256=(Get-FileHash -LiteralPath $manifest).Hash;PreservedExistingFileCount=$before.Count;PreservedExistingHashes=$before;NoExistingRegistrationChanged=$true;CreatedUtc=[DateTime]::UtcNow.ToString('O')}
$receipt | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $source 'deployment-receipt.json') -Encoding utf8
[pscustomobject]@{PackageId=$settings.PackageId;InstalledDirectory=$target;NewManifest=$manifest;PreservedExistingFileCount=$before.Count}
