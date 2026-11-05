@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
set LOG=%~dp0build-log.txt
echo ShaftChecker build %date% %time% > "%LOG%"

echo [1/4] 檢查 .NET SDK ...
where dotnet >> "%LOG%" 2>&1
if errorlevel 1 (
  echo 找不到 dotnet。請先安裝 .NET 8 SDK： https://dotnet.microsoft.com/download/dotnet/8.0
  echo NO_DOTNET >> "%LOG%"
  pause
  exit /b 1
)
dotnet --list-sdks >> "%LOG%" 2>&1

echo [2/4] 檢查 Revit 是否已關閉 ...
tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe" >nul
if not errorlevel 1 (
  echo Revit 正在執行，DLL 會被鎖住。請先關閉 Revit 再重新執行本檔。
  echo REVIT_RUNNING >> "%LOG%"
  pause
  exit /b 1
)

echo [3/4] 執行單元測試 ...
echo ===== TEST ===== >> "%LOG%"
dotnet test tests\ShaftChecker.Tests\ShaftChecker.Tests.csproj -nologo >> "%LOG%" 2>&1
if errorlevel 1 (echo 單元測試有失敗，詳見 build-log.txt) else (echo 單元測試全部通過)

echo [4/4] 建置 Revit 2024 外掛並部署 ...
echo ===== BUILD ===== >> "%LOG%"
dotnet build src\ShaftChecker.Revit\ShaftChecker.Revit.csproj -c Debug -p:RevitVersion=2024 -nologo >> "%LOG%" 2>&1
if errorlevel 1 (
  echo 建置失敗，詳見 build-log.txt
  echo BUILD_FAILED >> "%LOG%"
) else (
  echo 建置成功，已部署到 %%APPDATA%%\Autodesk\Revit\Addins\2024\
  echo BUILD_OK >> "%LOG%"
  dir "%APPDATA%\Autodesk\Revit\Addins\2024\ShaftChecker" >> "%LOG%" 2>&1
  dir "%APPDATA%\Autodesk\Revit\Addins\2024\ShaftChecker.addin" >> "%LOG%" 2>&1
)
echo.
echo 完成。可以關閉此視窗，並回到 Claude。
pause
