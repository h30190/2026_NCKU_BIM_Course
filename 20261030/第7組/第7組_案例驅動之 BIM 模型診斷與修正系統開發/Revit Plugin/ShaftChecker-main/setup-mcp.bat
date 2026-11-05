@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
set LOG=%~dp0setup-mcp-log.txt
echo ShaftChecker MCP setup %date% %time% > "%LOG%"

echo [1/3] 尋找 Python 3.10 以上 ...
set PY=
for %%P in (python py) do (
  if not defined PY (
    %%P -c "import sys; sys.exit(0 if sys.version_info>=(3,10) else 1)" >nul 2>&1 && set PY=%%P
  )
)
if not defined PY (
  echo 找不到 Python 3.10 以上。請安裝 https://www.python.org/downloads/ （安裝時勾選 Add python.exe to PATH）
  echo NO_PYTHON >> "%LOG%"
  pause
  exit /b 1
)
%PY% --version >> "%LOG%" 2>&1
%PY% -c "import sys; print(sys.executable)" >> "%LOG%" 2>&1

echo [2/3] 安裝 mcp 套件 ...
%PY% -m pip install -r mcp_server\requirements.txt >> "%LOG%" 2>&1
if errorlevel 1 (
  echo 安裝 mcp 套件失敗，詳見 setup-mcp-log.txt
  echo PIP_FAILED >> "%LOG%"
  pause
  exit /b 1
)
%PY% -c "import mcp, importlib.metadata as m; print('mcp', m.version('mcp'))" >> "%LOG%" 2>&1

echo [3/3] 註冊到 Claude 桌面版 ...
%PY% mcp_server\configure_claude.py >> "%LOG%" 2>&1
if errorlevel 1 (
  echo 註冊失敗，詳見 setup-mcp-log.txt
  echo CONFIG_FAILED >> "%LOG%"
) else (
  echo 完成！請「完全結束」Claude 桌面版（系統匣圖示按右鍵 - 結束）後重新開啟。
  echo SETUP_OK >> "%LOG%"
)
echo.
pause
