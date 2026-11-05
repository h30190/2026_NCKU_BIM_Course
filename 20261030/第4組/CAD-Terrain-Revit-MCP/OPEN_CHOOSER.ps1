$ErrorActionPreference='Stop'
$python=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'python-path.txt') -Raw).Trim()
& $python (Join-Path $PSScriptRoot 'src\choose_cad.py')
