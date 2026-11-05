param([string]$TestUrl = '', [ValidateSet('encoding','panel','agent','shaft_startup')][string]$Suite = 'encoding')
$ErrorActionPreference = 'Stop'
$caseGraphRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$caseGraphEngine = 'C:\Program Files\pyRevit-Master\bin\netfx\engines\IPY2712PR'
foreach ($caseGraphDll in @('pyRevitLabs.Microsoft.Scripting.dll','pyRevitLabs.Microsoft.Dynamic.dll','pyRevitLabs.IronPython.dll','pyRevitLabs.IronPython.Modules.dll')) {
    $null = [Reflection.Assembly]::LoadFrom((Join-Path $caseGraphEngine $caseGraphDll))
}
$caseGraphLoader = [Reflection.Assembly]::LoadFrom((Join-Path $caseGraphEngine 'pyRevitLoader.dll'))
$caseGraphArchive = Join-Path $caseGraphRoot 'backend\data\ironpython-lib.zip'
$caseGraphStream = $caseGraphLoader.GetManifestResourceStream('pyRevitLoader.python_2712pr_lib.zip')
$caseGraphFile = [IO.File]::Create($caseGraphArchive)
try { $caseGraphStream.CopyTo($caseGraphFile) } finally { $caseGraphFile.Dispose(); $caseGraphStream.Dispose() }
# Same engine options as pyRevit's Revit runtime (pyRevitLabs.PyRevit.Runtime): some IronPython
# bugs, e.g. list comprehensions inside generators, only appear with Tracing + FullFrames.
$caseGraphOptions = New-Object 'System.Collections.Generic.Dictionary[string,object]'
foreach ($caseGraphOption in @('Frames', 'FullFrames', 'LightweightScopes', 'Tracing')) { $caseGraphOptions[$caseGraphOption] = $true }
$caseGraphCompiler = [IronPython.Hosting.Python]::CreateEngine($caseGraphOptions)
$caseGraphCompiler.Runtime.LoadAssembly([Reflection.Assembly]::LoadFrom((Join-Path $caseGraphEngine 'pyRevitLabs.IronPython.Modules.dll')))
if ($Suite -eq 'panel') {
    foreach ($caseGraphAssembly in @('WindowsBase','PresentationCore','PresentationFramework','System.Xaml')) {
        Add-Type -AssemblyName $caseGraphAssembly
    }
    $caseGraphCompiler.Runtime.LoadAssembly([Reflection.Assembly]::LoadFrom((Join-Path $caseGraphEngine 'pyRevitLabs.IronPython.Wpf.dll')))
}
$caseGraphCompiler.SetSearchPaths([string[]]@($caseGraphArchive,(Join-Path $caseGraphRoot 'CaseBase.extension\lib')))
$env:CASEGRAPH_TEST_URL = $TestUrl
try { $null = $caseGraphCompiler.ExecuteFile((Join-Path $PSScriptRoot ('ironpython_' + $Suite + '.py'))) }
finally { Remove-Item Env:\CASEGRAPH_TEST_URL -ErrorAction SilentlyContinue }
