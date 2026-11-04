param(
    [Parameter(Mandatory=$true)][string]$Payload,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\CaseDrivenBIM'),
    [switch]$TestMode
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$Payload = (Resolve-Path -LiteralPath $Payload).Path
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$current = Join-Path $InstallRoot 'app'
$stage = Join-Path $InstallRoot ('stage-' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $InstallRoot ('previous-' + [Guid]::NewGuid().ToString('N'))
$cli = $null
$promoted = $false
$hadPrevious = $false
$registered = $false

function Assert-Child([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installation path escaped its destination.'
    }
    if ((Test-Path -LiteralPath $resolved) -and
        ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Installation destination cannot be a junction or symbolic link.'
    }
}

function Check-Package([string]$Folder) {
    $manifest = Get-Content -LiteralPath (Join-Path $Folder 'package-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.product -ne 'CaseDrivenBIM' -or $manifest.revit -ne '2022') { throw 'Unsupported installation package.' }
    $expected = @{'package-manifest.json' = $true}
    foreach ($entry in $manifest.files) { $expected[$entry.path.Replace('/', '\')] = $true }
    foreach ($item in Get-ChildItem -LiteralPath $Folder -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package contains a junction or symbolic link.' }
        if (-not $item.PSIsContainer) {
            $relative = $item.FullName.Substring($Folder.Length + 1)
            if (-not $expected.ContainsKey($relative)) { throw ('Unexpected package file: ' + $relative) }
        }
    }
    foreach ($entry in $manifest.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $Folder $entry.path))
        if (-not $path.StartsWith($Folder + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid manifest path.' }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Package file missing: ' + $entry.path) }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw ('Package checksum failed: ' + $entry.path) }
    }
}

function Check-Prerequisites {
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'Windows x64 is required.' }
    if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Please save your work and close Revit first. / 請先儲存並關閉 Revit。' }
    $command = Get-Command pyrevit.exe -ErrorAction SilentlyContinue
    $candidates = @(
        $(if ($command) { $command.Source }),
        (Join-Path $env:ProgramFiles 'pyRevit-Master\bin\pyrevit.exe'),
        (Join-Path $env:LOCALAPPDATA 'pyRevit-Master\bin\pyrevit.exe'),
        (Join-Path $env:APPDATA 'pyRevit-Master\bin\pyrevit.exe')
    )
    $script:cli = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    if (-not $script:cli) { throw 'Install pyRevit with its CLI and attach it to Revit 2022 first. / 請先安裝 pyRevit 並連接 Revit 2022。' }
    $attached = $false
    foreach ($base in @($env:APPDATA, $env:ProgramData)) {
        $addin = Join-Path $base 'Autodesk\Revit\Addins\2022\pyRevit.addin'
        if (Test-Path -LiteralPath $addin) {
            [xml]$xml = Get-Content -LiteralPath $addin -Raw
            foreach ($assembly in $xml.SelectNodes('//Assembly')) {
                $assemblyPath = $assembly.InnerText.Trim()
                if (-not [IO.Path]::IsPathRooted($assemblyPath)) { $assemblyPath = Join-Path (Split-Path $addin) $assemblyPath }
                if (Test-Path -LiteralPath $assemblyPath -PathType Leaf) { $attached = $true }
            }
        }
    }
    if (-not $attached) { throw 'A working pyRevit attachment for Revit 2022 was not found.' }
}

try {
    Check-Package $Payload
    if (-not $TestMode) { Check-Prerequisites }
    if ($InstallRoot -eq [IO.Path]::GetPathRoot($InstallRoot) -or $InstallRoot -eq $env:USERPROFILE) { throw 'Invalid installation root.' }
    if ((Test-Path -LiteralPath $InstallRoot) -and ((Get-Item -LiteralPath $InstallRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installation root cannot be a junction.' }
    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
    Assert-Child $stage
    Assert-Child $current
    Assert-Child $backup
    if (Test-Path -LiteralPath $current) {
        if (-not (Test-Path -LiteralPath (Join-Path $current 'package-manifest.json'))) { throw 'Destination contains an unmanaged installation; choose another folder.' }
        $running = Get-Process python,pythonw -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($current + '\', [StringComparison]::OrdinalIgnoreCase) }
        if ($running) { throw 'Close the installed BIM backend before updating. Existing files were kept.' }
    }
    Copy-Item -LiteralPath $Payload -Destination $stage -Recurse
    # User records/settings are never supplied by the package or overwritten by an upgrade.
    foreach ($relative in @('backend\data','.mcp.json','ShaftChecker-main\src\ShaftChecker.Revit\config\shaft-rules.json')) {
        $saved = Join-Path $current $relative
        if (Test-Path -LiteralPath $saved) {
            $target = Join-Path $stage $relative
            Copy-Item -LiteralPath $saved -Destination $target -Recurse -Force
        }
    }
    $python = Join-Path $stage 'runtime\python\python.exe'
    & $python (Join-Path $stage 'Verify-Installation.py') --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Runtime verification failed. Existing installation was kept.' }
    if (Test-Path -LiteralPath $current) { Move-Item -LiteralPath $current -Destination $backup; $hadPrevious = $true }
    Move-Item -LiteralPath $stage -Destination $current
    $promoted = $true
    if (-not $TestMode) {
        & $cli extensions paths add $current
        if ($LASTEXITCODE -ne 0) { throw 'pyRevit extension registration failed.' }
        $registered = $true
        $menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Case-driven BIM'
        New-Item -ItemType Directory -Path $menu -Force | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        foreach ($item in @(@('English Guide','README.md'), @('中文使用說明','README.zh-TW.md'))) {
            $link = $shell.CreateShortcut((Join-Path $menu ($item[0] + '.lnk')))
            $link.TargetPath = Join-Path $current $item[1]
            $link.Save()
        }
        $link = $shell.CreateShortcut((Join-Path $menu 'Check Installation.lnk'))
        $link.TargetPath = Join-Path $current 'runtime\python\python.exe'
        $link.Arguments = '"' + (Join-Path $current 'Verify-Installation.py') + '" --self-test --pause'
        $link.Save()
        $uninstall = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $current 'Uninstall.ps1') + '"'
        $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CaseDrivenBIM'
        New-Item -Path $key -Force | Out-Null
        New-ItemProperty $key -Name DisplayName -Value 'Case-driven BIM Diagnosis and Repair / 案例驅動 BIM 診斷與修正' -Force | Out-Null
        New-ItemProperty $key -Name DisplayVersion -Value '2026.11.04.2' -Force | Out-Null
        New-ItemProperty $key -Name InstallLocation -Value $current -Force | Out-Null
        New-ItemProperty $key -Name UninstallString -Value $uninstall -Force | Out-Null
    }
    Write-Output 'Installation and isolated runtime check passed. / 安裝及獨立環境檢查通過。'
} catch {
    if ($promoted) {
        Assert-Child $current
        Move-Item -LiteralPath $current -Destination ($stage + '-failed')
        if ($hadPrevious) { Move-Item -LiteralPath $backup -Destination $current }
        elseif ($registered) { & $cli extensions paths forget $current }
    }
    Write-Error $_
    exit 1
} finally {
    if (Test-Path -LiteralPath $stage) { Assert-Child $stage; Remove-Item -LiteralPath $stage -Recurse -Force }
}
