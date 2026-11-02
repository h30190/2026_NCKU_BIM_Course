# 編譯 LEBR 碳排外掛並安裝到 Revit（預設 2027）。安裝後重新啟動 Revit，功能區會出現「LEBR」頁籤。
# 用法：powershell -ExecutionPolicy Bypass -File install.ps1 [-RevitVersion 2027]
# -Folder：Revit 執行中舊版 DLL 會被鎖住，可裝到新資料夾（例如 LebrCarbon_2），重開 Revit 後生效
param([string]$RevitVersion = "2027", [string]$Folder = "LebrCarbon")

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$config = "Release.R" + $RevitVersion.Substring(2)

dotnet build (Join-Path $here "LebrCarbon.csproj") -c $config -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "編譯失敗" }

$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$target = Join-Path $addins $Folder
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item -Recurse -Force (Join-Path $here "bin\$config\*") $target

$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>LEBR 碳排計算</Name>
    <Assembly>$target\LebrCarbon.dll</Assembly>
    <FullClassName>LebrCarbon.App</FullClassName>
    <AddInId>3C9B4F1E-7A2D-4B8E-9F61-2D5A8C0E4B17</AddInId>
    <VendorId>LEBR</VendorId>
    <VendorDescription>低碳（低蘊含碳）建築評估 LEBR 計算外掛</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
[System.IO.File]::WriteAllText((Join-Path $addins "LebrCarbon.addin"), $manifest, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "已安裝到 $target"
Write-Host "請重新啟動 Revit $RevitVersion，功能區會出現「LEBR」頁籤。"
