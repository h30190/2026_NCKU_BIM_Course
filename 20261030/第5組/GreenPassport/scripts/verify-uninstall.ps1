$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$uninstall = Join-Path $root 'revit-plugin\uninstall.ps1'
$dll = Join-Path $root 'revit-plugin\bin\Release.R24\net48\TemporalBim.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw '請先建置外掛再驗證解除安裝。' }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('GreenPassportUninstallTest-' + [guid]::NewGuid().ToString('N'))
$addin = Join-Path $fixture 'Autodesk\Revit\Addins\2024'
$releases = Join-Path $addin 'TemporalBimReleases\1234567890ABCDEF'
New-Item -ItemType Directory -Path $releases -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $releases 'TemporalBim.dll')
Copy-Item -LiteralPath (Join-Path $root 'revit-plugin\TemporalBim.addin') -Destination (Join-Path $addin 'TemporalBim.addin')
Copy-Item -LiteralPath (Join-Path $root 'revit-plugin\TemporalBim.addin') -Destination (Join-Path $addin 'OtherTool.addin')
$hash = (Get-FileHash -LiteralPath $dll).Hash
& $uninstall -Version 2024 -AddinDirectory $addin -WhatIf
if (-not (Test-Path -LiteralPath (Join-Path $releases 'TemporalBim.dll'))) { throw 'WhatIf 改動了版本檔案。' }
if (@(Get-ChildItem -LiteralPath $addin -Directory -Filter 'TemporalBimRemoved-*').Count -ne 0) { throw 'WhatIf 不應建立備份。' }
$rejected = $false
try { & $uninstall -Version 2024 -AddinDirectory $fixture } catch { $rejected = $true }
if (-not $rejected) { throw '未拒絕錯誤的根目錄。' }
& $uninstall -Version 2024 -AddinDirectory $addin
$backups = @(Get-ChildItem -LiteralPath $addin -Directory -Filter 'TemporalBimRemoved-*')
if ($backups.Count -ne 1) { throw '未建立唯一的可回復備份。' }
$backup = $backups[0].FullName
foreach ($name in @('TemporalBim.addin','TemporalBimReleases')) {
    if (Test-Path -LiteralPath (Join-Path $addin $name)) { throw ('原位置仍有部署檔：' + $name) }
    if (-not (Test-Path -LiteralPath (Join-Path $backup $name))) { throw ('備份缺少：' + $name) }
}
if ((Get-FileHash -LiteralPath (Join-Path $backup 'TemporalBimReleases\1234567890ABCDEF\TemporalBim.dll')).Hash -ne $hash) { throw '備份 DLL 不一致。' }
if (-not (Test-Path -LiteralPath (Join-Path $addin 'OtherTool.addin'))) { throw '不應更動其他外掛。' }
& $uninstall -Version 2024 -AddinDirectory $addin
Write-Output 'PASS: uninstall dry-run, path boundary, recoverable version cleanup, unrelated add-in preservation and repeat operation.'
