param([switch]$TestMode)
$ErrorActionPreference = 'Stop'
$app = [IO.Path]::GetFullPath($PSScriptRoot)
$root = Split-Path $app -Parent
if ((Split-Path $app -Leaf) -ne 'app' -or -not (Test-Path -LiteralPath (Join-Path $app 'package-manifest.json'))) { throw 'Unrecognized installation.' }
if ((Get-Item -LiteralPath $app).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installation cannot be a junction.' }
$running = Get-Process python,pythonw -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($app + '\', [StringComparison]::OrdinalIgnoreCase)
}
if ($running) { throw 'Close the installed BIM backend before uninstalling.' }
if (-not $TestMode) {
    if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Close Revit before uninstalling.' }
    $command = Get-Command pyrevit.exe -ErrorAction SilentlyContinue
    $cli = if ($command) { $command.Source } else { $null }
    if (-not $cli) {
        $cli = @($env:ProgramFiles, $env:LOCALAPPDATA, $env:APPDATA) | ForEach-Object { Join-Path $_ 'pyRevit-Master\bin\pyrevit.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (-not $cli) { throw 'pyRevit CLI was not found. Remove this extension path in pyRevit settings first.' }
    & $cli extensions paths forget $app
    if ($LASTEXITCODE -ne 0) { throw 'Could not unregister extension; files were kept.' }
}
$saved = Join-Path $root ('saved-data-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $saved | Out-Null
foreach ($relative in @('backend\data','.mcp.json','ShaftChecker-main\src\ShaftChecker.Revit\config\shaft-rules.json')) {
    $source = Join-Path $app $relative
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $saved -Recurse }
}
# Only the verified application folder is removed; saved data/previous versions stay.
if (-not $app.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid removal path.' }
Remove-Item -LiteralPath $app -Recurse -Force
if (-not $TestMode) {
    Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CaseDrivenBIM' -ErrorAction SilentlyContinue
    $menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Case-driven BIM'
    foreach ($name in @('English Guide.lnk','中文使用說明.lnk','Check Installation.lnk')) { Remove-Item -LiteralPath (Join-Path $menu $name) -ErrorAction SilentlyContinue }
}
Write-Output ('Uninstalled; your data is retained in: ' + $saved)
