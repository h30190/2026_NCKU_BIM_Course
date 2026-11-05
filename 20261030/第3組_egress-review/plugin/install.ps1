# Install the add-in for this user. With no -RevitVersion it installs into every Revit 2024-2027 found on this computer.
param([ValidateSet('auto','2024','2025','2026','2027')][string]$RevitVersion = 'auto')
$ErrorActionPreference = 'Stop'
$versions = @('2024','2025','2026','2027')

function Get-Framework([string]$v) {
    switch ($v) { '2024' { 'net48' } '2027' { 'net10.0-windows' } default { 'net8.0-windows' } }
}
# A universal package keeps one DLL per version in a folder named after it; a single-version package has the DLL
# next to this script; a source checkout has it under bin.
function Find-Source([string]$v) {
    $candidates = @((Join-Path $PSScriptRoot "$v\Group3.EgressExporter.dll"))
    if ($RevitVersion -ne 'auto') { $candidates += (Join-Path $PSScriptRoot 'Group3.EgressExporter.dll') }
    $candidates += (Join-Path $PSScriptRoot "bin\$v\Release\$(Get-Framework $v)\Group3.EgressExporter.dll")
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
    return $null
}
function Test-RevitInstalled([string]$v) {
    if (Test-Path -LiteralPath "C:\Program Files\Autodesk\Revit $v\Revit.exe") { return $true }
    if (Test-Path -LiteralPath "HKLM:\SOFTWARE\Autodesk\Revit\Autodesk Revit $v") { return $true }
    return $false
}
function Install-One([string]$v, [string]$source) {
    $root = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
    $packageRoot = Join-Path $root 'Group3.EgressExporter'
    # Keep loaded assemblies intact. Revit reads the updated manifest on its next start.
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    $target = Join-Path $packageRoot $hash
    [IO.Directory]::CreateDirectory($target) | Out-Null
    $assembly = Join-Path $target 'Group3.EgressExporter.dll'
    if (!(Test-Path -LiteralPath $assembly)) { Copy-Item -LiteralPath $source -Destination $assembly }
    # Files from a downloaded zip are marked as coming from the internet, and Revit will not load them.
    Unblock-File -LiteralPath $assembly
    if ((Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) {
        throw 'Installed assembly hash mismatch; manifest was not changed.'
    }
    $escapedAssembly = [Security.SecurityElement]::Escape($assembly)
    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Group3 Egress Exporter</Name>
    <Assembly>$escapedAssembly</Assembly>
    <AddInId>627C658A-AC26-456A-B0F5-2E3EA2FC7664</AddInId>
    <FullClassName>Group3.EgressExporter.App</FullClassName>
    <VendorId>G3EG</VendorId>
    <VendorDescription>Group 3 read-only egress data exporter</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
    $manifestPath = Join-Path $root 'Group3.EgressExporter.addin'
    $pendingManifest = Join-Path $root ('Group3.EgressExporter.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($pendingManifest, $manifest, [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $manifestPath) {
        $backup = Join-Path $packageRoot ('manifest-' + [Guid]::NewGuid().ToString('N') + '.backup')
        [IO.File]::Replace($pendingManifest, $manifestPath, $backup)
    } else {
        [IO.File]::Move($pendingManifest, $manifestPath)
    }
    Write-Output "Revit ${v}: prepared for next Revit start: $assembly"
}

$targets = if ($RevitVersion -eq 'auto') { $versions | Where-Object { Test-RevitInstalled $_ } } else { @($RevitVersion) }
if (!$targets) { throw 'No Revit 2024-2027 found on this computer. Run again with -RevitVersion 2024 (or 2025, 2026, 2027).' }
$installed = 0
foreach ($v in $targets) {
    $source = Find-Source $v
    if (!$source) { Write-Output "Revit ${v}: no add-in build in this package, skipped."; continue }
    Install-One $v $source
    $installed++
}
if ($installed -eq 0) { throw 'Nothing was installed.' }
Write-Output 'Save your model and restart Revit to load the add-in.'
