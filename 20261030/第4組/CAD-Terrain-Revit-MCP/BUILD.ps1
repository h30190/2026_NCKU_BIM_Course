param([string]$RevitFolder = 'C:\Program Files\Autodesk\Revit 2024')
$ErrorActionPreference='Stop'
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$api=Join-Path $RevitFolder 'RevitAPI.dll'
$ui=Join-Path $RevitFolder 'RevitAPIUI.dll'
if (!(Test-Path -LiteralPath $api)) { throw 'Revit 2024 API not found.' }
New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'bin') -Force | Out-Null
$out=Join-Path $PSScriptRoot 'bin\CadTerrainKitV3Icons.dll'
& $csc /nologo /target:library /platform:x64 /optimize+ "/out:$out" "/reference:$api" "/reference:$ui" /reference:System.Web.Extensions.dll /reference:System.Core.dll "/reference:$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll" "/reference:$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll" (Join-Path $PSScriptRoot 'src\RevitBridge.cs') (Join-Path $PSScriptRoot 'src\RibbonIcons.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Built $out"
