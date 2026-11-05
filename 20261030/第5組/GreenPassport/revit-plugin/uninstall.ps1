# 移除 Temporal BIM 外掛（跟 install.ps1 配對）：
#   .\uninstall.ps1 -Version 2024
# 注意：Revit 要先關掉，不然 DLL 被咬住刪不掉。
[CmdletBinding(SupportsShouldProcess=$true)]
param([ValidatePattern('^\d{4}$')][string]$Version='2024', [string]$AddinDirectory)
$ErrorActionPreference='Stop'
if (-not $AddinDirectory -and (Get-Process -Name Revit -ErrorAction SilentlyContinue) -and -not $WhatIfPreference) {
    throw '請先儲存模型並關閉 Revit，再執行解除安裝。'
}
$directory=if ($AddinDirectory) { $AddinDirectory } else { Join-Path $env:APPDATA ('Autodesk\Revit\Addins\'+$Version) }
$addinDir=[IO.Path]::GetFullPath($directory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$info=New-Object IO.DirectoryInfo($addinDir)
if ($info.Name -ne $Version -or $info.Parent.Name -ne 'Addins' -or $info.Parent.Parent.Name -ne 'Revit' -or $info.Parent.Parent.Parent.Name -ne 'Autodesk') {
    throw '目標必須是 Autodesk\Revit\Addins\指定年份 的版本資料夾。'
}
$manifest=Join-Path $addinDir 'TemporalBim.addin'
if (Test-Path -LiteralPath $manifest) {
    [xml]$xml=Get-Content -LiteralPath $manifest -Raw -Encoding UTF8
    if (@($xml.RevitAddIns.AddIn | Where-Object { $_.AddInId -eq '6C2E1F3A-5B9D-4E7F-8A1C-2D3B4E5F6A7B' }).Count -ne 1) { throw '此 manifest 不屬於 Green Passport，未更動。' }
}
$targets=@('TemporalBim.addin','TemporalBimReleases','TemporalBimCurrent','TemporalBim') |
    ForEach-Object { Join-Path $addinDir $_ } | Where-Object { Test-Path -LiteralPath $_ }
foreach ($target in $targets) {
    $resolved=(Resolve-Path -LiteralPath $target).Path
    if (-not $resolved.StartsWith($addinDir+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw '目標超出指定資料夾。' }
    if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '目標是連結或接合點，未更動。' }
}
if (-not $targets) { Write-Output '找不到已安裝的 Green Passport。'; return }
$backup=Join-Path $addinDir ('TemporalBimRemoved-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))
if ($PSCmdlet.ShouldProcess($addinDir,'將 Green Passport 檔案移到可回復備份')) {
    New-Item -ItemType Directory -Path $backup | Out-Null
    foreach ($target in $targets) { Move-Item -LiteralPath $target -Destination $backup }
    Write-Output ('已停用；檔案保留在：'+$backup)
    Write-Output '要回復時，關閉 Revit 後將備份內項目移回上一層，再重新啟動。'
}
