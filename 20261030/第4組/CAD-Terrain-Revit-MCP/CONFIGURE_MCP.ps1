param([string]$PythonExe)
$ErrorActionPreference='Stop'
if (!$PythonExe) {
    $bundled=Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
    if (Test-Path -LiteralPath $bundled) { $PythonExe=$bundled }
    else { $PythonExe=(Get-Command python -ErrorAction Stop).Source }
}
if (!(Test-Path -LiteralPath $PythonExe)) { throw 'Python not found. Supply -PythonExe with Python 3.12 x64.' }
$server=Join-Path $PSScriptRoot 'src\mcp_server.py'
$toml=@"
# Add this section to your Codex config; do not replace the whole config.
[mcp_servers.cad_terrain_kit]
command = '$PythonExe'
args = ['$server']
startup_timeout_sec = 20
tool_timeout_sec = 300
"@
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'codex-mcp.generated.toml'),$toml,[Text.UTF8Encoding]::new($false))
$json=@{mcpServers=@{cad_terrain_kit=@{command=$PythonExe;args=@($server)}}}|ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'mcp.generated.json'),$json,[Text.UTF8Encoding]::new($false))
Write-Host 'Created codex-mcp.generated.toml and mcp.generated.json. Existing client settings were not changed.'
