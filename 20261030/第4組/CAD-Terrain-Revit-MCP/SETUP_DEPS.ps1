param([Parameter(Mandatory=$true)][string]$PythonExe)
$ErrorActionPreference='Stop'
& $PythonExe -c "import sys,struct,tkinter; assert sys.version_info[:2]==(3,12) and struct.calcsize('P')==8"
if ($LASTEXITCODE -ne 0) { throw 'Python 3.12 x64 with tkinter is required.' }
& $PythonExe -m pip install --only-binary=:all: --target (Join-Path $PSScriptRoot 'vendor') -r (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
