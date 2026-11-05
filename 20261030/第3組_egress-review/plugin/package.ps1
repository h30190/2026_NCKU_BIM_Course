# Build the add-in for Revit 2024-2027 and make one zip for other computers. The target computer only needs Revit.
# Versions without a local Revit are built against the Revit API reference packages from NuGet (Nice3point.Revit.Api).
param(
    [string[]]$RevitVersions = @('2024','2025','2026','2027'),
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$apiPackages = @{ '2024' = '2024.3.60'; '2025' = '2025.4.60'; '2026' = '2026.4.10'; '2027' = '2027.3.0' }

function Get-ApiDirectory([string]$v) {
    $local = "C:\Program Files\Autodesk\Revit $v"
    if (Test-Path -LiteralPath (Join-Path $local 'RevitAPI.dll')) { return $local }
    $dir = Join-Path $PSScriptRoot "obj\revit-api\$v"
    if ((Test-Path -LiteralPath (Join-Path $dir 'RevitAPI.dll')) -and (Test-Path -LiteralPath (Join-Path $dir 'RevitAPIUI.dll'))) { return $dir }
    [IO.Directory]::CreateDirectory($dir) | Out-Null
    foreach ($name in @('RevitAPI','RevitAPIUI')) {
        $id = "nice3point.revit.api.$($name.ToLowerInvariant())"
        $version = $apiPackages[$v]
        $nupkg = Join-Path $dir "$id.$version.nupkg"
        Write-Host "Downloading $name $version from NuGet..."
        Invoke-WebRequest -UseBasicParsing -Uri "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg" -OutFile $nupkg
        $zip = [IO.Compression.ZipFile]::OpenRead($nupkg)
        try {
            $entry = $zip.Entries | Where-Object { $_.FullName -like 'ref/*' -and $_.Name -eq "$name.dll" } | Select-Object -First 1
            if (!$entry) { throw "$name.dll not found in $id $version" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $dir "$name.dll"), $true)
        } finally { $zip.Dispose() }
        Remove-Item -LiteralPath $nupkg
    }
    return $dir
}
function Get-Framework([string]$v) {
    switch ($v) { '2024' { 'net48' } '2027' { 'net10.0-windows' } default { 'net8.0-windows' } }
}

$dist = Join-Path $PSScriptRoot 'dist'
$name = 'EgressExporter-Revit' + ($RevitVersions | Sort-Object | Select-Object -First 1) +
    $(if ($RevitVersions.Count -gt 1) { '-' + ($RevitVersions | Sort-Object | Select-Object -Last 1) } else { '' })
$folder = Join-Path $dist $name
if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
[IO.Directory]::CreateDirectory($folder) | Out-Null
foreach ($v in $RevitVersions) {
    $api = Get-ApiDirectory $v
    & (Join-Path $PSScriptRoot 'build.ps1') -RevitVersion $v -Dotnet $Dotnet -RevitDirectory $api | Out-Null
    $dll = Join-Path $PSScriptRoot "bin\$v\Release\$(Get-Framework $v)\Group3.EgressExporter.dll"
    [IO.Directory]::CreateDirectory((Join-Path $folder $v)) | Out-Null
    Copy-Item -LiteralPath $dll -Destination (Join-Path $folder $v)
    Write-Output "Built Revit $v"
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination $folder
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '使用說明.txt') -Destination $folder
$bat = "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0install.ps1`"`r`npause`r`n"
[IO.File]::WriteAllText((Join-Path $folder 'install.bat'), $bat, [Text.Encoding]::ASCII)
$zip = Join-Path $dist "$name.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip)
Write-Output "Package: $zip"
