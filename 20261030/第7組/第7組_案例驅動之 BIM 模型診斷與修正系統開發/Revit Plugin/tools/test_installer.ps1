$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$build = (Resolve-Path -LiteralPath (Join-Path $workspace '.build')).Path
$payloadRoot = Get-Content -LiteralPath (Join-Path $build 'last-release.txt') -Raw -Encoding UTF8
$destination = Join-Path $build ('install-test-' + [Guid]::NewGuid().ToString('N'))
function Install-Test {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $payloadRoot 'Install.ps1') -Payload (Join-Path $payloadRoot 'app') -InstallRoot $destination -TestMode
    if ($LASTEXITCODE -ne 0) { throw 'Isolated installation failed.' }
}
Install-Test
$app = Join-Path $destination 'app'
if (-not (Test-Path -LiteralPath (Join-Path $app 'CaseBase.extension/startup.py'))) { throw 'Extension missing.' }
$data = Join-Path $app 'backend\data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $data 'preserve.txt'), 'existing-user-data')
[IO.File]::WriteAllText((Join-Path $app '.mcp.json'), '{"mcpServers":{},"local":"preserve"}')
$rule = Join-Path $app 'ShaftChecker-main\src\ShaftChecker.Revit\config\shaft-rules.json'
$settings = [IO.File]::ReadAllText($rule) + "`n "
[IO.File]::WriteAllText($rule, $settings)
Install-Test
if ([IO.File]::ReadAllText((Join-Path $data 'preserve.txt')) -ne 'existing-user-data') { throw 'Upgrade lost user data.' }
if ([IO.File]::ReadAllText((Join-Path $app '.mcp.json')) -notmatch 'preserve') { throw 'Upgrade lost local configuration.' }
if ([IO.File]::ReadAllText($rule) -ne $settings) { throw 'Upgrade lost rule settings.' }
if (@(Get-ChildItem -LiteralPath $destination -Directory -Filter 'previous-*').Count -ne 1) { throw 'Previous version was not retained.' }
$readme = Join-Path $payloadRoot 'app\README.md'
$original = [IO.File]::ReadAllBytes($readme)
try {
    [IO.File]::WriteAllText($readme, 'tampered package')
    $ErrorActionPreference = 'Continue'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $payloadRoot 'Install.ps1') -Payload (Join-Path $payloadRoot 'app') -InstallRoot $destination -TestMode 2>&1 | Out-Null
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -eq 0) { throw 'Tampered payload was accepted.' }
} finally { $ErrorActionPreference = 'Stop'; [IO.File]::WriteAllBytes($readme, $original) }
if ([IO.File]::ReadAllText((Join-Path $data 'preserve.txt')) -ne 'existing-user-data') { throw 'Rejected upgrade changed user data.' }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $app 'Uninstall.ps1') -TestMode
if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $app)) { throw 'Uninstall failed.' }
$saved = Get-ChildItem -LiteralPath $destination -Directory -Filter 'saved-data-*' | Select-Object -First 1
if ([IO.File]::ReadAllText((Join-Path $saved.FullName 'data\preserve.txt')) -ne 'existing-user-data') { throw 'Uninstall lost user data.' }
Write-Output 'PASS: install, upgrade, settings/data preservation, tamper rejection and uninstall in isolated workspace.'
